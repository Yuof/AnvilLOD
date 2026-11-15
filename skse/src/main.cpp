#include "Adaptive.h"
#include "Controller.h"
#include "LodDistances.h"
#include "LodLayers.h"
#include "Menu.h"
#include "Settings.h"
#include <Windows.h>

namespace
{
	void SetupLog()
	{
		auto dir = logger::log_directory();
		if (!dir) {
			return;
		}
		auto path = *dir / "PenguLOD.log";
		PenguLOD::Menu::SetLogPath(path.string());
		auto sink = std::make_shared<spdlog::sinks::basic_file_sink_mt>(path.string(), true);
		auto log = std::make_shared<spdlog::logger>("global", std::move(sink));
		log->set_level(spdlog::level::info);
		log->flush_on(spdlog::level::info);
		spdlog::set_default_logger(std::move(log));
		spdlog::set_pattern("[%H:%M:%S.%e] [%l] %v");
	}

	void OnMessage(SKSE::MessagingInterface::Message* a_msg)
	{
		// Another DLL may load after ours. Suppress every callback if both runtimes are present.
		if (GetModuleHandleW(L"AnvilLOD.dll")) {
			if (a_msg->type == SKSE::MessagingInterface::kDataLoaded)
				logger::critical("AnvilLOD.dll is also loaded; PenguLOD controllers will remain stopped. Enable only one runtime plugin.");
			return;
		}
		auto& controller = PenguLOD::Controller::Get();
		switch (a_msg->type) {
		case SKSE::MessagingInterface::kDataLoaded:
			PenguLOD::Settings::Get().Load();
			// Related runtime fixes: recommended alongside PenguLOD, never duplicated by it.
			logger::info("SkyrimLODFixes.dll (LOD Refresh Bug Fix): {}", PenguLOD::LodDistances::PluginInstalled("SkyrimLODFixes.dll") ? "installed" : "not installed (recommended)");
			logger::info("DynDOLOD.dll: {}", PenguLOD::LodDistances::PluginInstalled("DynDOLOD.dll") ? "installed" : "not installed");
			PenguLOD::LodDistances::Get().Apply("startup");
			PenguLOD::LodDistances::Get().StartWatching();
			controller.OnDataLoaded();
			controller.Start();
			PenguLOD::Layers::Get().Load();
			PenguLOD::Layers::Get().Start();
			PenguLOD::Adaptive::Get().Start();
			PenguLOD::Menu::Register();
			break;
		case SKSE::MessagingInterface::kPostLoadGame:
			PenguLOD::LodDistances::Get().Apply("game loaded");
			break;
		case SKSE::MessagingInterface::kPreLoadGame:
		case SKSE::MessagingInterface::kNewGame:
			controller.Reset();
			break;
		default:
			break;
		}
	}
}

SKSEPluginLoad(const SKSE::LoadInterface* a_skse)
{
	// The two build lines link different CommonLibs (Skyrim 1.7 changed Address Library's file format and many
	// structs), and CommonLib's Init already opens Address Library, so the wrong DLL has to stop before Init.
	// SKSE logs the refusal in skse64.log.
	const auto game = a_skse->RuntimeVersion();
	// Line 1: every 1.5 and 1.6 runtime, SE 1.5.97 up to AE 1.6.1170 on Steam and 1.6.1179 on GOG.
	// Line 17: Skyrim 1.7 and newer (1.7.99, 1.7.104, ...). The split is the 1.7 format change, not a build number:
	// GOG's last 1.6 build is numbered above Steam's.
	constexpr REL::Version kFirstNew{ 1, 7, 0, 0 };
	constexpr REL::Version kFirstSE{ 1, 5, 0, 0 };   // Skyrim VR is 1.4.15; every SE/AE runtime is 1.5 or newer
#if PENGULOD_RUNTIME_LINE == 17
	constexpr auto kLine = "Skyrim 1.7 and newer";
	if (game < kFirstNew) return false;
#elif PENGULOD_RUNTIME_LINE == 14
	constexpr auto kLine = "Skyrim VR 1.4.15";
	if (game >= kFirstSE) return false;
#else
	constexpr auto kLine = "SE 1.5.97 to AE 1.6.1170 (GOG 1.6.1179)";
	if (game >= kFirstNew || game < kFirstSE) return false;
#endif

	SKSE::Init(a_skse);
	SetupLog();
	if (GetModuleHandleW(L"AnvilLOD.dll")) {
		logger::critical("AnvilLOD.dll is also loaded. Disable the old runtime plugin before enabling PenguLOD.dll; two LOD controllers cannot run together.");
		return false;
	}
	logger::info("PenguLOD SKSE plugin {} loading. Build line: {}; game {}", PENGULOD_VERSION_STRING, kLine, game.string());
	PenguLOD::Menu::SetGameVersion(game.string());

	if (!SKSE::GetMessagingInterface()->RegisterListener(OnMessage)) {
		logger::critical("Could not register for SKSE messages");
		return false;
	}
	return true;
}
