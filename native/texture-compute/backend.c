// PenguLOD optional headless Vulkan compute backend. GPL-3.0-only.
// Persistent 4-slot worker pool with 4-row-aligned horizontal strip batching up to 8192x8192.
#include "bc7_spv.h"
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <vulkan/vulkan_core.h>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#define PGL_EXPORT __declspec(dllexport)
typedef SRWLOCK pgl_mutex_t;
static void pgl_mutex_init(pgl_mutex_t *m) { InitializeSRWLock(m); }
static void pgl_mutex_destroy(pgl_mutex_t *m) { (void)m; }
static void pgl_mutex_lock(pgl_mutex_t *m) { AcquireSRWLockExclusive(m); }
static void pgl_mutex_unlock(pgl_mutex_t *m) { ReleaseSRWLockExclusive(m); }
#else
#include <pthread.h>
#define PGL_EXPORT __attribute__((visibility("default")))
typedef pthread_mutex_t pgl_mutex_t;
static void pgl_mutex_init(pgl_mutex_t *m) { pthread_mutex_init(m, NULL); }
static void pgl_mutex_destroy(pgl_mutex_t *m) { pthread_mutex_destroy(m); }
static void pgl_mutex_lock(pgl_mutex_t *m) { pthread_mutex_lock(m); }
static void pgl_mutex_unlock(pgl_mutex_t *m) { pthread_mutex_unlock(m); }
#endif

#define PGL_SLOTS 4
#define PGL_INPUT_CAP (32ull * 1024ull * 1024ull)
#define PGL_OUTPUT_CAP (8ull * 1024ull * 1024ull)

typedef struct {
  VkBuffer buffer;
  VkDeviceMemory memory;
  void *mapped;
  VkDeviceSize size;
} Buffer;

typedef struct {
  VkCommandPool command_pool;
  VkCommandBuffer command;
  VkDescriptorPool descriptor_pool;
  VkDescriptorSet set;
  VkFence fence;
  Buffer input;
  Buffer output;
  int in_use;
} Slot;

typedef struct {
  VkInstance instance;
  VkPhysicalDevice physical;
  VkDevice device;
  VkQueue queue;
  VkDescriptorSetLayout descriptor_layout;
  VkPipelineLayout layout;
  VkPipeline pipeline;
  VkPhysicalDeviceMemoryProperties memory;
  VkPhysicalDeviceProperties properties;
  pgl_mutex_t slot_lock;
  pgl_mutex_t queue_lock;
  int locks_ready;
  Slot slots[PGL_SLOTS];
} Context;

static void free_buffer(Context *c, Buffer *b) {
  if (b->mapped)
    vkUnmapMemory(c->device, b->memory);
  if (b->buffer)
    vkDestroyBuffer(c->device, b->buffer, NULL);
  if (b->memory)
    vkFreeMemory(c->device, b->memory, NULL);
  memset(b, 0, sizeof(*b));
}

static VkResult alloc_buffer(Context *c, VkDeviceSize size, Buffer *b) {
  VkBufferCreateInfo info = {.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
                             .size = size,
                             .usage = VK_BUFFER_USAGE_STORAGE_BUFFER_BIT};
  VkResult result = vkCreateBuffer(c->device, &info, NULL, &b->buffer);
  if (result)
    return result;
  VkMemoryRequirements requirements;
  vkGetBufferMemoryRequirements(c->device, b->buffer, &requirements);
  uint32_t type = UINT32_MAX;
  // Prefer DEVICE_LOCAL | HOST_VISIBLE | HOST_COHERENT (ReBAR / unified memory) when available, else HOST_VISIBLE | HOST_COHERENT.
  for (int pass = 0; pass < 2 && type == UINT32_MAX; pass++) {
    VkMemoryPropertyFlags want =
        pass == 0 ? (VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT |
                     VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT |
                     VK_MEMORY_PROPERTY_HOST_COHERENT_BIT)
                  : (VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT |
                     VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    for (uint32_t i = 0; i < c->memory.memoryTypeCount; i++)
      if ((requirements.memoryTypeBits & (1u << i)) &&
          (c->memory.memoryTypes[i].propertyFlags & want) == want) {
        type = i;
        break;
      }
  }
  if (type == UINT32_MAX)
    return VK_ERROR_FEATURE_NOT_PRESENT;
  VkMemoryAllocateInfo allocation = {
      .sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
      .allocationSize = requirements.size,
      .memoryTypeIndex = type};
  result = vkAllocateMemory(c->device, &allocation, NULL, &b->memory);
  if (result)
    return result;
  result = vkBindBufferMemory(c->device, b->buffer, b->memory, 0);
  if (result)
    return result;
  b->size = size;
  return vkMapMemory(c->device, b->memory, 0, size, 0, &b->mapped);
}

PGL_EXPORT void pgl_destroy(Context *c) {
  if (!c)
    return;
  if (c->device) {
    vkDeviceWaitIdle(c->device);
    for (int i = 0; i < PGL_SLOTS; i++) {
      Slot *s = &c->slots[i];
      if (s->fence)
        vkDestroyFence(c->device, s->fence, NULL);
      free_buffer(c, &s->input);
      free_buffer(c, &s->output);
      if (s->command_pool)
        vkDestroyCommandPool(c->device, s->command_pool, NULL);
      if (s->descriptor_pool)
        vkDestroyDescriptorPool(c->device, s->descriptor_pool, NULL);
    }
    if (c->pipeline)
      vkDestroyPipeline(c->device, c->pipeline, NULL);
    if (c->layout)
      vkDestroyPipelineLayout(c->device, c->layout, NULL);
    if (c->descriptor_layout)
      vkDestroyDescriptorSetLayout(c->device, c->descriptor_layout, NULL);
    vkDestroyDevice(c->device, NULL);
  }
  if (c->instance)
    vkDestroyInstance(c->instance, NULL);
  if (c->locks_ready) {
    pgl_mutex_destroy(&c->slot_lock);
    pgl_mutex_destroy(&c->queue_lock);
  }
  free(c);
}

#define CHECK(call)                                                            \
  do {                                                                         \
    VkResult rc = (call);                                                      \
    if (rc != VK_SUCCESS) {                                                    \
      snprintf(message, capacity, "%s failed (%d)", #call, rc);                \
      goto failed;                                                             \
    }                                                                          \
  } while (0)

PGL_EXPORT Context *pgl_create(char *message, int capacity, int *software) {
  Context *c = calloc(1, sizeof(*c));
  if (!c)
    return NULL;
  pgl_mutex_init(&c->slot_lock);
  pgl_mutex_init(&c->queue_lock);
  c->locks_ready = 1;

  VkShaderModule module = VK_NULL_HANDLE;
  VkApplicationInfo app = {.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO,
                           .pApplicationName = "PenguLOD",
                           .apiVersion = VK_API_VERSION_1_0};
  VkInstanceCreateInfo info = {.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
                               .pApplicationInfo = &app};
  CHECK(vkCreateInstance(&info, NULL, &c->instance));
  uint32_t count = 0;
  CHECK(vkEnumeratePhysicalDevices(c->instance, &count, NULL));
  if (count == 0 || count > 64) {
    snprintf(message, capacity, "No usable Vulkan device.");
    goto failed;
  }
  VkPhysicalDevice devices[64];
  CHECK(vkEnumeratePhysicalDevices(c->instance, &count, devices));
  uint32_t family = 0;
  int best = -1;
  for (uint32_t i = 0; i < count; i++) {
    VkPhysicalDeviceProperties p;
    vkGetPhysicalDeviceProperties(devices[i], &p);
    uint32_t families = 0;
    vkGetPhysicalDeviceQueueFamilyProperties(devices[i], &families, NULL);
    if (families > 128)
      continue;
    VkQueueFamilyProperties queues[128];
    vkGetPhysicalDeviceQueueFamilyProperties(devices[i], &families, queues);
    int rank = p.deviceType == VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU     ? 4
               : p.deviceType == VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU ? 3
               : p.deviceType == VK_PHYSICAL_DEVICE_TYPE_CPU            ? 0
                                                                        : 1;
    for (uint32_t j = 0; j < families; j++)
      if ((queues[j].queueFlags & VK_QUEUE_COMPUTE_BIT) &&
          queues[j].queueCount && rank > best) {
        c->physical = devices[i];
        c->properties = p;
        family = j;
        best = rank;
        break;
      }
  }
  if (best < 0) {
    snprintf(message, capacity, "No compute queue.");
    goto failed;
  }
  *software = c->properties.deviceType == VK_PHYSICAL_DEVICE_TYPE_CPU;
  vkGetPhysicalDeviceMemoryProperties(c->physical, &c->memory);
  float priority = 1;
  VkDeviceQueueCreateInfo queue = {
      .sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
      .queueFamilyIndex = family,
      .queueCount = 1,
      .pQueuePriorities = &priority};
  VkDeviceCreateInfo device = {.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,
                               .queueCreateInfoCount = 1,
                               .pQueueCreateInfos = &queue};
  CHECK(vkCreateDevice(c->physical, &device, NULL, &c->device));
  vkGetDeviceQueue(c->device, family, 0, &c->queue);
  VkDescriptorSetLayoutBinding bindings[2] = {
      {.binding = 0,
       .descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER,
       .descriptorCount = 1,
       .stageFlags = VK_SHADER_STAGE_COMPUTE_BIT},
      {.binding = 1,
       .descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER,
       .descriptorCount = 1,
       .stageFlags = VK_SHADER_STAGE_COMPUTE_BIT}};
  VkDescriptorSetLayoutCreateInfo descriptors = {
      .sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO,
      .bindingCount = 2,
      .pBindings = bindings};
  CHECK(vkCreateDescriptorSetLayout(c->device, &descriptors, NULL,
                                    &c->descriptor_layout));
  VkPushConstantRange push = {.stageFlags = VK_SHADER_STAGE_COMPUTE_BIT,
                              .size = 8};
  VkPipelineLayoutCreateInfo layout = {
      .sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO,
      .setLayoutCount = 1,
      .pSetLayouts = &c->descriptor_layout,
      .pushConstantRangeCount = 1,
      .pPushConstantRanges = &push};
  CHECK(vkCreatePipelineLayout(c->device, &layout, NULL, &c->layout));
  VkShaderModuleCreateInfo shader = {
      .sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO,
      .codeSize = sizeof(bc7_spv),
      .pCode = bc7_spv};
  CHECK(vkCreateShaderModule(c->device, &shader, NULL, &module));
  VkComputePipelineCreateInfo pipeline = {
      .sType = VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO,
      .layout = c->layout,
      .stage = {.sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO,
                .stage = VK_SHADER_STAGE_COMPUTE_BIT,
                .module = module,
                .pName = "main"}};
  CHECK(vkCreateComputePipelines(c->device, VK_NULL_HANDLE, 1, &pipeline, NULL,
                                 &c->pipeline));
  vkDestroyShaderModule(c->device, module, NULL);
  module = VK_NULL_HANDLE;

  VkDeviceSize max_range = c->properties.limits.maxStorageBufferRange;
  VkDeviceSize in_cap = PGL_INPUT_CAP < max_range ? PGL_INPUT_CAP : max_range;
  VkDeviceSize out_cap = PGL_OUTPUT_CAP < max_range ? PGL_OUTPUT_CAP : max_range;
  if (in_cap < 8192ull * 4ull * 4ull || out_cap < (8192ull / 4ull) * 16ull) {
    snprintf(message, capacity, "Storage buffer limit too small.");
    goto failed;
  }

  for (int i = 0; i < PGL_SLOTS; i++) {
    Slot *s = &c->slots[i];
    VkCommandPoolCreateInfo pool = {
        .sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
        .flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT,
        .queueFamilyIndex = family};
    CHECK(vkCreateCommandPool(c->device, &pool, NULL, &s->command_pool));
    VkCommandBufferAllocateInfo cmd_alloc = {
        .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
        .commandPool = s->command_pool,
        .level = VK_COMMAND_BUFFER_LEVEL_PRIMARY,
        .commandBufferCount = 1};
    CHECK(vkAllocateCommandBuffers(c->device, &cmd_alloc, &s->command));
    VkDescriptorPoolSize pool_size = {.type = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER,
                                      .descriptorCount = 2};
    VkDescriptorPoolCreateInfo desc_pool = {
        .sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO,
        .maxSets = 1,
        .poolSizeCount = 1,
        .pPoolSizes = &pool_size};
    CHECK(vkCreateDescriptorPool(c->device, &desc_pool, NULL, &s->descriptor_pool));
    VkDescriptorSetAllocateInfo desc_alloc = {
        .sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO,
        .descriptorPool = s->descriptor_pool,
        .descriptorSetCount = 1,
        .pSetLayouts = &c->descriptor_layout};
    CHECK(vkAllocateDescriptorSets(c->device, &desc_alloc, &s->set));
    VkFenceCreateInfo fence_info = {.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
    CHECK(vkCreateFence(c->device, &fence_info, NULL, &s->fence));
    CHECK(alloc_buffer(c, in_cap, &s->input));
    CHECK(alloc_buffer(c, out_cap, &s->output));
    VkDescriptorBufferInfo buf_info[2] = {{s->input.buffer, 0, in_cap},
                                          {s->output.buffer, 0, out_cap}};
    VkWriteDescriptorSet writes[2] = {0};
    for (uint32_t b = 0; b < 2; b++)
      writes[b] = (VkWriteDescriptorSet){
          .sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET,
          .dstSet = s->set,
          .dstBinding = b,
          .descriptorCount = 1,
          .descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER,
          .pBufferInfo = &buf_info[b]};
    vkUpdateDescriptorSets(c->device, 2, writes, 0, NULL);
  }
  snprintf(message, capacity, "%s", c->properties.deviceName);
  return c;
failed:
  if (module)
    vkDestroyShaderModule(c->device, module, NULL);
  pgl_destroy(c);
  return NULL;
}

static Slot *acquire_slot(Context *c) {
  Slot *found = NULL;
  pgl_mutex_lock(&c->slot_lock);
  for (int i = 0; i < PGL_SLOTS; i++) {
    if (!c->slots[i].in_use) {
      c->slots[i].in_use = 1;
      found = &c->slots[i];
      break;
    }
  }
  pgl_mutex_unlock(&c->slot_lock);
  return found;
}

static void release_slot(Context *c, Slot *s) {
  pgl_mutex_lock(&c->slot_lock);
  s->in_use = 0;
  pgl_mutex_unlock(&c->slot_lock);
}

// Returns 1 on success, -1 if all 4 slots are busy (caller falls back to CPU for that tile without disabling GPU), 0 on error.
PGL_EXPORT int pgl_encode(Context *c, const uint8_t *rgba, uint32_t width,
                          uint32_t height, uint8_t *output, char *message,
                          int capacity) {
  if (!c || !rgba || !output || width == 0 || height == 0 || width > 8192 ||
      height > 8192) {
    snprintf(message, capacity, "Invalid image dimensions.");
    return 0;
  }
  Slot *s = acquire_slot(c);
  if (!s)
    return -1;

  int success = 0;
  uint32_t bw = (width + 3u) / 4u;
  uint64_t row_in_bytes = (uint64_t)width * 4ull;
  uint64_t block_row_out_bytes = (uint64_t)bw * 16ull;
  uint32_t max_rows_in = (uint32_t)((s->input.size / row_in_bytes) & ~3ull);
  uint32_t max_rows_out =
      (uint32_t)((s->output.size / block_row_out_bytes) * 4ull);
  uint32_t strip_rows = max_rows_in < max_rows_out ? max_rows_in : max_rows_out;
  if (strip_rows < 4u) {
    snprintf(message, capacity, "Strip capacity below 4 rows.");
    goto failed;
  }

  for (uint32_t y0 = 0; y0 < height; y0 += strip_rows) {
    uint32_t sh = height - y0;
    if (sh > strip_rows)
      sh = strip_rows;
    uint32_t sbh = (sh + 3u) / 4u;
    uint64_t in_bytes = (uint64_t)width * sh * 4ull;
    uint64_t out_bytes = (uint64_t)bw * sbh * 16ull;

    memcpy(s->input.mapped, rgba + (uint64_t)y0 * width * 4ull, in_bytes);
    CHECK(vkResetFences(c->device, 1, &s->fence));
    CHECK(vkResetCommandBuffer(s->command, 0));
    VkCommandBufferBeginInfo begin = {
        .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
        .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT};
    CHECK(vkBeginCommandBuffer(s->command, &begin));
    VkMemoryBarrier barrier = {.sType = VK_STRUCTURE_TYPE_MEMORY_BARRIER,
                               .srcAccessMask = VK_ACCESS_HOST_WRITE_BIT,
                               .dstAccessMask = VK_ACCESS_SHADER_READ_BIT};
    vkCmdPipelineBarrier(s->command, VK_PIPELINE_STAGE_HOST_BIT,
                         VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT, 0, 1, &barrier,
                         0, NULL, 0, NULL);
    vkCmdBindPipeline(s->command, VK_PIPELINE_BIND_POINT_COMPUTE, c->pipeline);
    vkCmdBindDescriptorSets(s->command, VK_PIPELINE_BIND_POINT_COMPUTE,
                            c->layout, 0, 1, &s->set, 0, NULL);
    uint32_t dims[2] = {width, sh};
    vkCmdPushConstants(s->command, c->layout, VK_SHADER_STAGE_COMPUTE_BIT, 0, 8,
                       dims);
    vkCmdDispatch(s->command, (uint32_t)((out_bytes / 16ull + 63ull) / 64ull),
                  1, 1);
    barrier.srcAccessMask = VK_ACCESS_SHADER_WRITE_BIT;
    barrier.dstAccessMask = VK_ACCESS_HOST_READ_BIT;
    vkCmdPipelineBarrier(s->command, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
                         VK_PIPELINE_STAGE_HOST_BIT, 0, 1, &barrier, 0, NULL, 0,
                         NULL);
    CHECK(vkEndCommandBuffer(s->command));

    VkSubmitInfo submit = {.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO,
                           .commandBufferCount = 1,
                           .pCommandBuffers = &s->command};
    pgl_mutex_lock(&c->queue_lock);
    VkResult sub_rc = vkQueueSubmit(c->queue, 1, &submit, s->fence);
    pgl_mutex_unlock(&c->queue_lock);
    if (sub_rc != VK_SUCCESS) {
      snprintf(message, capacity, "vkQueueSubmit failed (%d)", sub_rc);
      goto failed;
    }
    CHECK(vkWaitForFences(c->device, 1, &s->fence, VK_TRUE, 30000000000ull));
    memcpy(output + (uint64_t)(y0 / 4u) * bw * 16ull, s->output.mapped,
           out_bytes);
  }
  success = 1;
failed:
  if (!success) {
    pgl_mutex_lock(&c->queue_lock);
    vkDeviceWaitIdle(c->device);
    pgl_mutex_unlock(&c->queue_lock);
  }
  release_slot(c, s);
  return success;
}
