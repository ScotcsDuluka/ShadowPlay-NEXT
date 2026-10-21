#pragma once
#include <nlohmann/json.hpp>
#include <algorithm>
#include <cmath>
#include <chrono>
#include <stdexcept>
#include <string>
#include <vector>

namespace ansel {
using Json = nlohmann::json;

struct Failure : std::runtime_error {
    int status;
    std::string code;
    Failure(int status, const char *code, const char *message)
        : std::runtime_error(message), status(status), code(code) {}
};

// All methods are called on the effect runtime's render thread. Wire IDs never
// contain ReShade handles; the implementation resolves them for each operation.
struct Runtime {
    virtual ~Runtime() = default;
    virtual Json catalog() = 0;
    virtual void enable(const std::string &id, bool enabled) = 0;
    virtual void value(const std::string &id, const std::string &control, const Json &values) = 0;
    // Native runtimes can read back a known uniform handle without walking
    // every effect and uniform on the game's present thread.
    virtual Json read_value(const std::string &id, const std::string &control) {
        for (const auto &filter : catalog()) if (filter.at("id") == id)
            for (const auto &item : filter.at("controls")) if (item.at("id") == control)
                return item.at("currentValue");
        throw Failure(404, "FILTER_NOT_FOUND", "Filter or control no longer exists");
    }
    virtual void reorder(const std::vector<std::string> &ids) = 0;
    virtual Json preset_info() const { return {{"supported", false}}; }
    virtual void select_preset(unsigned) { throw Failure(501, "NOT_SUPPORTED", "ReShade preset switching is unavailable"); }
    virtual void save_preset() { throw Failure(501, "NOT_SUPPORTED", "ReShade preset saving is unavailable"); }
    virtual void toggle_preset_effects(bool) { throw Failure(501, "NOT_SUPPORTED", "ReShade global effects toggle is unavailable"); }
};

class FilterSession {
    Runtime &runtime_;
    unsigned long pid_;
    std::string session_;
    unsigned generation_ = 0;
    bool enabled_ = true;
    Json baseline_;
    std::vector<std::string> stack_;

    static const Json &find(const Json &items, const std::string &id) {
        for (const auto &item : items) if (item.at("id") == id) return item;
        throw Failure(404, "FILTER_NOT_FOUND", "Filter or control no longer exists");
    }
    static size_t index(const Json &body) {
        if (!body.contains("stackIdx") || !body["stackIdx"].is_number_integer())
            throw Failure(400, "INVALID_ARGUMENT", "stackIdx must be an integer");
        const auto value = body["stackIdx"].get<int>();
        if (value < 0 || value >= 64) throw Failure(400, "INVALID_ARGUMENT", "stackIdx is out of range");
        return static_cast<size_t>(value);
    }
    static Json validated(const Json &control, Json values) {
        if (!values.is_array()) values = Json::array({values});
        if (values.size() != control.at("currentValue").size())
            throw Failure(400, "INVALID_ARGUMENT", "Uniform dimensions do not match");
        const std::string type = control.at("dataType");
        for (size_t i = 0; i < values.size(); ++i) {
            if (type == "bool") {
                if (values[i] == 0) values[i] = false;
                if (values[i] == 1) values[i] = true;
                if (!values[i].is_boolean()) throw Failure(400, "INVALID_ARGUMENT", "Boolean value required");
            } else {
                if (!values[i].is_number() || !std::isfinite(values[i].get<double>()))
                    throw Failure(400, "INVALID_ARGUMENT", "Finite numeric value required");
                double v = values[i].get<double>();
                if ((type == "int" || type == "uint") && std::floor(v) != v)
                    throw Failure(400, "INVALID_ARGUMENT", "Integer value required");
                const double lower = control.at("minValue")[i].get<double>(), upper = control.at("maxValue")[i].get<double>();
                if (type == "float") {
                    // OSC sends decimal JSON while ReShade bounds are float32.
                    // Accept boundary rounding, but never a meaningful overflow.
                    if (v < lower && lower-v <= 0.000001 * (std::max)(1.0,std::abs(lower))) values[i] = v = lower;
                    if (v > upper && v-upper <= 0.000001 * (std::max)(1.0,std::abs(upper))) values[i] = v = upper;
                }
                if ((v < lower || v > upper) &&
                    values[i] != control.at("currentValue")[i])
                    throw Failure(400, "OUT_OF_RANGE", "Uniform value is outside its supported range");
            }
        }
        return values;
    }
    void write_value(const std::string &id, const std::string &control, const Json &values) {
        runtime_.value(id, control, values);
        const auto actual = runtime_.read_value(id, control);
        if (actual.size() != values.size()) throw Failure(409, "RUNTIME_REJECTED", "Runtime uniform dimensions changed");
        for (size_t i=0; i<values.size(); ++i) {
            const bool equal = values[i].is_boolean() ? actual[i] == values[i] :
                actual[i].is_number() && std::abs(actual[i].get<double>() - values[i].get<double>()) <=
                    0.00001 * (std::max)(1.0, std::abs(values[i].get<double>()));
            if (!equal) throw Failure(409, "RUNTIME_REJECTED", "ReShade did not apply the requested uniform value");
        }
    }
    void apply_stack(const std::vector<std::string> &next) {
        // Only techniques selected by OSC are disabled, never unrelated effects.
        for (const auto &id : stack_)
            if (std::find(next.begin(), next.end(), id) == next.end()) runtime_.enable(id, false);
        for (const auto &id : next) runtime_.enable(id, enabled_);
        runtime_.reorder(next);
        stack_ = next;
    }
public:
    FilterSession(Runtime &runtime, unsigned long pid, std::string session)
        : runtime_(runtime), pid_(pid), session_(std::move(session)) { reload(); }
    void reload() {
        ++generation_;
        baseline_ = runtime_.catalog();
        stack_.clear();
        enabled_ = true;
        for (const auto &filter : baseline_)
            if (filter.at("enabled").get<bool>()) stack_.push_back(filter.at("id"));
    }
    Json snapshot() {
        const auto preset = runtime_.preset_info();
        // In mirror mode the ReShade runtime, not an OSC localStorage cache,
        // owns the currently enabled technique list and its saved parameters.
        auto filters = runtime_.catalog();
        if (preset.value("supported", false)) {
            stack_.clear();
            for (const auto &filter : filters)
                if (filter.at("enabled").get<bool>()) stack_.push_back(filter.at("id"));
        }
        return {{"protocol", 1}, {"pid", pid_}, {"session", session_},
            {"generation", generation_}, {"enabled", enabled_}, {"stack", stack_},
            {"preset", preset}, {"filters", std::move(filters)}};
    }
    Json dispatch(const Json &request) {
        try {
            if (!request.is_object() || request.value("protocol", 0) != 1)
                throw Failure(400, "PROTOCOL_MISMATCH", "Ansel protocol version 1 is required");
            const auto unixMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::system_clock::now().time_since_epoch()).count();
            if (!request.contains("deadline") || !request["deadline"].is_number_integer() || request["deadline"].get<int64_t>() < unixMs)
                throw Failure(408, "EXPIRED_COMMAND", "Command expired before the game could process it");
            const auto op = request.at("op").get<std::string>();
            if (op == "state") return {{"ok", true}, {"state", snapshot()}};
            if (request.value("session", std::string()) != session_ || request.value("generation", 0u) != generation_)
                throw Failure(409, "STALE_SESSION", "Runtime or effect catalog changed; refresh before retrying");
            const auto body = request.value("body", Json::object());
            if (op == "presetSelect") {
                if (!body.contains("slot") || !body["slot"].is_number_integer() ||
                    body["slot"].get<int>() < 1 || body["slot"].get<int>() > 3)
                    throw Failure(400, "INVALID_ARGUMENT", "Preset slot must be 1, 2 or 3");
                const auto slot = body["slot"].get<unsigned>();
                const bool alreadyActive = runtime_.preset_info().value("activeSlot", 0u) == slot;
                if (alreadyActive) {
                    runtime_.toggle_preset_effects(true);
                    enabled_ = true;
                } else {
                    runtime_.select_preset(slot);
                    // New ReShade preset owns the stack and uniform values.
                    reload();
                }
                return {{"ok", true}, {"state", snapshot()}};
            }
            if (op == "presetSave") {
                runtime_.save_preset();
                return {{"ok", true}, {"state", snapshot()}};
            }
            if (op == "presetToggle") {
                if (!body.contains("enabled") || !body["enabled"].is_boolean())
                    throw Failure(400, "INVALID_ARGUMENT", "enabled must be a boolean");
                enabled_ = body["enabled"].get<bool>();
                runtime_.toggle_preset_effects(enabled_);
                return {{"ok", true}, {"state", snapshot()}};
            }
            const auto catalog = runtime_.catalog();
            if (op == "replace") {
                const auto &filters = body.at("filters");
                if (!filters.is_array() || filters.size() > 64)
                    throw Failure(400, "INVALID_ARGUMENT", "filters must be an array of at most 64 entries");
                std::vector<std::string> next;
                Json writes = Json::array();
                for (const auto &filter : filters) {
                    const auto slot = index(filter);
                    if (slot != next.size()) throw Failure(400, "INVALID_ARGUMENT", "Filter stack must be contiguous and ordered");
                    const std::string id = filter.at("id");
                    const auto &definition = find(catalog, id);
                    if (std::find(next.begin(), next.end(), id) != next.end())
                        throw Failure(409, "DUPLICATE_FILTER", "A technique can only occupy one stack slot");
                    next.push_back(id);
                    const auto controls = filter.value("controls", Json::array());
                    if (!controls.is_array()) throw Failure(400, "INVALID_ARGUMENT", "controls must be an array");
                    for (const auto &control : controls) {
                        const std::string controlId = control.at("id");
                        const auto &description = find(definition.at("controls"), controlId);
                        writes.push_back({{"id",id}, {"control",controlId}, {"values",validated(description, control.at("value"))}});
                    }
                }
                // Validate the complete preset before the first runtime write.
                for (const auto &write : writes) write_value(write.at("id"), write.at("control"), write.at("values"));
                apply_stack(next);
            } else if (op == "select") {
                const size_t slot = index(body);
                const std::string id = body.at("type");
                if (slot > stack_.size()) throw Failure(400, "INVALID_ARGUMENT", "Filter stack has a gap");
                auto next = stack_;
                if (id == "None") { if (slot < next.size()) next.erase(next.begin() + slot); }
                else {
                    find(catalog, id);
                    const auto existing = std::find(next.begin(), next.end(), id);
                    if (existing != next.end() && static_cast<size_t>(existing - next.begin()) != slot)
                        throw Failure(409, "DUPLICATE_FILTER", "A technique can only occupy one stack slot");
                    if (slot == next.size()) next.push_back(id); else next[slot] = id;
                }
                apply_stack(next);
            } else if (op == "attribute") {
                const auto slot = index(body);
                const std::string id = body.at("id"), controlId = body.at("controlId");
                if (slot >= stack_.size() || stack_[slot] != id)
                    throw Failure(409, "STALE_STACK", "Filter does not occupy the requested stack slot");
                const auto &control = find(find(catalog, id).at("controls"), controlId);
                write_value(id, controlId, validated(control, body.at("value")));
            } else if (op == "clear") apply_stack({});
            else if (op == "mods") {
                if (!body.contains("globalEnable") || !body["globalEnable"].is_boolean())
                    throw Failure(400, "INVALID_ARGUMENT", "globalEnable must be a boolean");
                enabled_ = body["globalEnable"].get<bool>();
                for (const auto &id : stack_) runtime_.enable(id, enabled_);
            } else if (op == "reset") {
                const auto slot = index(body);
                if (slot >= stack_.size()) throw Failure(404, "FILTER_NOT_FOUND", "Stack slot is empty");
                const auto &filter = find(baseline_, stack_[slot]);
                for (const auto &control : filter.at("controls"))
                    write_value(stack_[slot], control.at("id"), control.at("currentValue"));
            } else throw Failure(501, "NOT_SUPPORTED", "Operation is not implemented by Ansel Adapter");
            return {{"ok", true}, {"state", snapshot()}};
        } catch (const Failure &error) {
            return {{"ok", false}, {"status", error.status}, {"code", error.code}, {"message", error.what()}};
        } catch (const Json::exception &) {
            return {{"ok", false}, {"status", 400}, {"code", "INVALID_ARGUMENT"}, {"message", "Invalid Ansel command"}};
        }
    }
};
}
