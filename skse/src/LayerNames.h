#pragma once
#include <charconv>
#include <string>
#include <string_view>

#include "RuntimePaths.h"

namespace PenguLOD::LayerNames
{
	inline constexpr const char* kKeys[3] = { "grass", "tree3d", "treecard" };
	inline constexpr int         kLevels[4] = { 4, 8, 16, 32 };

	/// "PL:grass:16" -> layer 0, level index 2, tier 0; "PL:grass:16:2" -> the same with tier 2. False for anything else.
	inline bool Parse(const char* a_name, int& a_layer, int& a_level, int& a_tier)
	{
		if (!a_name || !RuntimePaths::Layer(a_name)) {
			return false;
		}
		const std::string_view rest{ a_name + 3 };
		const auto             colon = rest.find(':');
		if (colon == std::string_view::npos) {
			return false;
		}
		a_layer = -1;
		for (int i = 0; i < 3; ++i) {
			if (rest.substr(0, colon) == kKeys[i]) a_layer = i;
		}
		a_level = -1;
		a_tier = 0;
		auto lv = rest.substr(colon + 1);
		if (const auto tc = lv.find(':'); tc != std::string_view::npos) {
			const auto t = lv.substr(tc + 1);
			if (t.size() != 1 || t[0] < '0' || t[0] > '9') {
				return false;
			}
			a_tier = t[0] - '0';
			lv = lv.substr(0, tc);
		}
		for (int i = 0; i < 4; ++i) {
			if (lv == std::to_string(kLevels[i])) a_level = i;
		}
		return a_layer >= 0 && a_level >= 0;
	}

	/// "PL:largeref:4:<cellX>:<cellY>" -> true and writes signed cell coordinates (native large-reference double-draw workaround).
	inline bool ParseLargeRef(const char* a_name, int& a_cellX, int& a_cellY)
	{
		if (!a_name || !RuntimePaths::Layer(a_name)) {
			return false;
		}
		const std::string_view rest{ a_name + 3 };
		constexpr std::string_view kPrefix = "largeref:4:";
		if (!rest.starts_with(kPrefix)) {
			return false;
		}
		const auto coords = rest.substr(kPrefix.size());
		const auto sep = coords.find(':');
		if (sep == std::string_view::npos || sep == 0 || sep + 1 >= coords.size()) {
			return false;
		}
		const auto sx = coords.substr(0, sep);
		const auto sy = coords.substr(sep + 1);
		int cx = 0, cy = 0;
		const auto rx = std::from_chars(sx.data(), sx.data() + sx.size(), cx);
		const auto ry = std::from_chars(sy.data(), sy.data() + sy.size(), cy);
		if (rx.ec != std::errc{} || rx.ptr != sx.data() + sx.size() ||
			ry.ec != std::errc{} || ry.ptr != sy.data() + sy.size()) {
			return false;
		}
		a_cellX = cx;
		a_cellY = cy;
		return true;
	}
}
