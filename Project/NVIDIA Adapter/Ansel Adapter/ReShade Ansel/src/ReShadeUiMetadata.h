#pragma once
#include <algorithm>
#include <cstdint>
#include <string>
#include <vector>

namespace reshade_ui {
// ReShade combo/radio labels are NUL-separated strings in ui_items.
// Never interpret a missing entry or unknown shader value as index zero.
inline std::vector<std::string> split_items(const std::string &raw) {
    std::vector<std::string> labels;
    size_t begin = 0;
    for (size_t i = 0; i <= raw.size(); ++i) {
        if (i != raw.size() && raw[i] != '\0') continue;
        if (i != begin && labels.size() < 64) labels.emplace_back(raw.substr(begin, i - begin));
        begin = i + 1;
    }
    return labels;
}
struct Choices {
    std::vector<std::string> labels;
    std::vector<int64_t> values;

    int index_of(int64_t value) const {
        auto it = std::find(values.begin(), values.end(), value);
        return it == values.end() ? -1 : static_cast<int>(it - values.begin());
    }
    bool valid_index(int64_t index) const {
        return index >= 0 && static_cast<size_t>(index) < values.size();
    }
    bool valid() const {
        return labels.size() >= 2 && labels.size() == values.size() && labels.size() <= 64;
    }
};
} // namespace reshade_ui
