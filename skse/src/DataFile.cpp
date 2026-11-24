#include "RuntimePaths.h"
#include "DataFile.h"

namespace PenguLOD
{
	namespace
	{
		class Reader
		{
		public:
			explicit Reader(std::vector<char> a_data) :
				_data(std::move(a_data)) {}

			template <class T>
			bool Get(T& a_out)
			{
				if (_pos + sizeof(T) > _data.size()) {
					return false;
				}
				std::memcpy(&a_out, _data.data() + _pos, sizeof(T));
				_pos += sizeof(T);
				return true;
			}

			/// True when a_count records of at least a_bytes each can still fit. A corrupted count would
			/// otherwise allocate gigabytes (std::bad_alloc inside the kDataLoaded handler).
			[[nodiscard]] bool Fits(std::uint32_t a_count, std::size_t a_bytes) const
			{
				return static_cast<std::uint64_t>(a_count) * a_bytes <= _data.size() - _pos;
			}

			bool GetString(std::string& a_out)
			{
				std::uint16_t len = 0;
				if (!Get(len) || _pos + len > _data.size()) {
					return false;
				}
				a_out.assign(_data.data() + _pos, len);
				_pos += len;
				return true;
			}

		private:
			std::vector<char> _data;
			std::size_t       _pos{ 0 };
		};
	}

	bool CopyFile::Read(const std::filesystem::path& a_path)
	{
		std::ifstream in(a_path, std::ios::binary);
		if (!in) {
			return false;  // the option is off, or Generate was run without it: not an error
		}
		std::vector<char> bytes((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
		Reader            r(std::move(bytes));

		std::array<char, 4> magic{};
		std::uint32_t       version = 0;
		if (!r.Get(magic) || !RuntimePaths::Magic(std::string_view(magic.data(), 4), "PLCP", "ALCP") || !r.Get(version)) {
			logger::error("{} is not a PenguLOD copies file", a_path.string());
			return false;
		}
		if (version < 1 || version > 2) {
			logger::error("{} has version {}, this plugin reads versions 1-2 - update the PenguLOD plugin or regenerate", a_path.string(), version);
			return false;
		}

		std::uint32_t count = 0;
		if (!r.Get(count) || !r.Fits(count, sizeof(std::uint16_t))) {
			logger::error("{}: truncated string table", a_path.string());
			return false;
		}
		strings.resize(count);
		for (auto& s : strings) {
			if (!r.GetString(s)) {
				logger::error("{}: truncated string table", a_path.string());
				return false;
			}
		}
		if (!r.Get(count) || !r.Fits(count, version >= 2 ? 46 : 44)) {
			logger::error("{}: truncated entry table", a_path.string());
			return false;
		}
		entries.resize(count);
		const auto n = static_cast<std::uint32_t>(strings.size());
		for (auto& e : entries) {
			if (!r.Get(e.worldPlugin) || !r.Get(e.worldLocalId) || !r.Get(e.model) || !r.Get(e.pos) || !r.Get(e.rot) || !r.Get(e.scale) || !r.Get(e.flags)) {
				logger::error("{}: truncated entry table", a_path.string());
				return false;
			}
			if (e.worldPlugin >= n || e.model >= n) {
				logger::error("{}: bad string index", a_path.string());
				return false;
			}
			if (version >= 2) {
				std::uint16_t altCount = 0;
				if (!r.Get(altCount) || !r.Fits(altCount, 12)) {
					logger::error("{}: truncated alternate texture table", a_path.string());
					return false;
				}
				e.alts.resize(altCount);
				for (auto& a : e.alts) {
					if (!r.Get(a.shape) || !r.Get(a.diffuse) || !r.Get(a.normal)) {
						logger::error("{}: truncated alternate texture table", a_path.string());
						return false;
					}
					if (a.shape >= n || (a.diffuse != CopyEntry::kNone && a.diffuse >= n) || (a.normal != CopyEntry::kNone && a.normal >= n)) {
						logger::error("{}: bad alternate texture string index", a_path.string());
						return false;
					}
				}
			}
		}
		return true;
	}

	bool NativeFile::Read(const std::filesystem::path& a_path)
	{
		std::ifstream in(a_path, std::ios::binary);
		if (!in) {
			return false;  // the option is off, or Generate was run without it: not an error
		}
		std::vector<char> bytes((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
		Reader            r(std::move(bytes));

		std::array<char, 4> magic{};
		std::uint32_t       version = 0;
		if (!r.Get(magic) || !RuntimePaths::Magic(std::string_view(magic.data(), 4), "PLNV", "ALNV") || !r.Get(version)) {
			logger::error("{} is not a PenguLOD native file", a_path.string());
			return false;
		}
		if (version != 1 && version != 2) {
			logger::error("{} has version {}, this plugin reads versions 1 and 2 - update the PenguLOD plugin or regenerate", a_path.string(), version);
			return false;
		}
		std::uint32_t count = 0;
		if (!r.Get(count) || !r.Fits(count, sizeof(std::uint16_t))) {
			logger::error("{}: truncated string table", a_path.string());
			return false;
		}
		strings.resize(count);
		for (auto& s : strings) {
			if (!r.GetString(s)) {
				logger::error("{}: truncated string table", a_path.string());
				return false;
			}
		}
		if (!r.Get(count) || !r.Fits(count, version >= 2 ? 40 : 28)) {  // 7 ids and flags, then x, y, z in version 2
			logger::error("{}: truncated entry table", a_path.string());
			return false;
		}
		entries.resize(count);
		for (auto& e : entries) {
			if (!r.Get(e.sourcePlugin) || !r.Get(e.sourceLocalId) || !r.Get(e.nativePlugin) || !r.Get(e.nativeLocalId) || !r.Get(e.parentPlugin) || !r.Get(e.parentLocalId) || !r.Get(e.flags)) {
				logger::error("{}: truncated entry table", a_path.string());
				return false;
			}
			if (version >= 2 && (!r.Get(e.x) || !r.Get(e.y) || !r.Get(e.z))) {
				logger::error("{}: truncated entry table", a_path.string());
				return false;
			}
			const auto n = static_cast<std::uint32_t>(strings.size());
			if (e.sourcePlugin >= n || e.nativePlugin >= n || (e.parentPlugin != NativeEntry::kNone && e.parentPlugin >= n)) {
				logger::error("{}: bad string index", a_path.string());
				return false;
			}
		}
		return true;
	}

	bool DataFile::Read(const std::filesystem::path& a_path)
	{
		std::ifstream in(a_path, std::ios::binary);
		if (!in) {
			logger::info("{} not found - no dynamic LOD (generate LOD with PenguLOD first)", a_path.string());
			return false;
		}
		std::vector<char> bytes((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
		Reader            r(std::move(bytes));

		std::array<char, 4> magic{};
		std::uint32_t       version = 0;
		if (!r.Get(magic) || !RuntimePaths::Magic(std::string_view(magic.data(), 4), "PLDY", "ALDY") || !r.Get(version)) {
			logger::error("{} is not a PenguLOD dynamic LOD file", a_path.string());
			return false;
		}
		// Version 2 added grid-object flags; version 3 adds mesh8 and mesh16 string indices. Versions 1 and 2 continue to load cleanly.
		if (version < 1 || version > 3) {
			logger::error("{} has version {}, this plugin reads versions 1-3 - update the PenguLOD plugin or regenerate", a_path.string(), version);
			return false;
		}

		std::uint32_t count = 0;
		if (!r.Get(count) || !r.Fits(count, sizeof(std::uint16_t))) {
			logger::error("{}: truncated string table", a_path.string());
			return false;
		}
		strings.resize(count);
		for (auto& s : strings) {
			if (!r.GetString(s)) {
				logger::error("{}: truncated string table", a_path.string());
				return false;
			}
		}

		if (!r.Get(count) || !r.Fits(count, version >= 3 ? 68 : 60)) {
			logger::error("{}: truncated entry table", a_path.string());
			return false;
		}
		entries.resize(count);
		const auto n = static_cast<std::uint32_t>(strings.size());
		for (auto& e : entries) {
			if (!r.Get(e.refPlugin) || !r.Get(e.refLocalId) || !r.Get(e.worldPlugin) || !r.Get(e.worldLocalId) || !r.Get(e.mesh)) {
				logger::error("{}: truncated entry table", a_path.string());
				return false;
			}
			if (version >= 3) {
				if (!r.Get(e.mesh8) || !r.Get(e.mesh16)) {
					logger::error("{}: truncated entry table", a_path.string());
					return false;
				}
			} else {
				e.mesh8 = DynamicEntry::kNone;
				e.mesh16 = DynamicEntry::kNone;
			}
			if (!r.Get(e.pos) || !r.Get(e.rot) || !r.Get(e.scale) || !r.Get(e.parentPlugin) || !r.Get(e.parentLocalId) || !r.Get(e.flags)) {
				logger::error("{}: truncated entry table", a_path.string());
				return false;
			}
			if (e.refPlugin >= n || e.worldPlugin >= n || e.mesh >= n ||
				(e.mesh8 != DynamicEntry::kNone && e.mesh8 >= n) ||
				(e.mesh16 != DynamicEntry::kNone && e.mesh16 >= n) ||
				(e.parentPlugin != DynamicEntry::kNone && e.parentPlugin >= n)) {
				logger::error("{}: bad string index", a_path.string());
				return false;
			}
		}
		return true;
	}
}
