#pragma once
#include <cstdint>
#include <deque>
#include <map>
#include <mutex>
#include <optional>
#include <string>

namespace osc {
class ToggleRequestGuard {
public:
    enum class Status { Queued, Duplicate, Invalid, Stale, Busy };
    struct Request { std::string id; std::uint64_t created; int intent; };
    struct Ticket { Status status; std::uint64_t token; };
    static std::optional<std::string> Header(const std::string& request, const std::string& name) {
        bool seen = false;
        std::string result;
        const auto firstLineEnd = request.find("\r\n");
        if (firstLineEnd == std::string::npos) return std::nullopt;
        const auto fold = [](char c) { return c >= 'A' && c <= 'Z' ? char(c + ('a' - 'A')) : c; };
        for (size_t start = firstLineEnd + 2; start < request.size();) {
            const auto end = request.find("\r\n", start);
            if (end == std::string::npos || end == start) break;
            const auto line = request.substr(start, end - start);
            bool matches = line.size() > name.size() && line[name.size()] == ':';
            for (size_t n = 0; matches && n < name.size(); ++n)
                matches = fold(line[n]) == fold(name[n]);
            if (matches) {
                if (seen) return std::nullopt;
                seen = true;
                result = line.substr(name.size() + 1);
                const auto first = result.find_first_not_of(" \t"), last = result.find_last_not_of(" \t");
                result = first == std::string::npos ? "" : result.substr(first, last - first + 1);
            }
            start = end + 2;
        }
        return result;
    }
    static bool SafeId(const std::string& id) {
        if (id.empty() || id.size() > 96) return false;
        for (const unsigned char c : id)
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == ':'))
                return false;
        return true;
    }
    static bool Fresh(std::uint64_t created, std::uint64_t now) {
        return created <= now ? now - created <= 2000 : created - now <= 1000;
    }
    Ticket Queue(Request request, std::uint64_t now) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (!SafeId(request.id) || request.intent < 0 || request.intent > 2)
            return {Status::Invalid, 0};
        if (!Fresh(request.created, now)) return {Status::Stale, 0};
        if (applied_.count(request.id)) return {Status::Duplicate, 0};
        for (auto it = pending_.begin(); it != pending_.end();) {
            if (!Fresh(it->second.created, now)) it = pending_.erase(it);
            else {
                if (it->second.id == request.id) return {Status::Busy, 0};
                ++it;
            }
        }
        if (pending_.size() >= 128) return {Status::Busy, 0};
        const auto token = ++next_;
        pending_.emplace(token, std::move(request));
        return {Status::Queued, token};
    }
    std::optional<Request> Consume(std::uint64_t token, std::uint64_t now) {
        std::lock_guard<std::mutex> lock(mutex_);
        auto it = pending_.find(token);
        if (it == pending_.end()) return std::nullopt;
        Request request = std::move(it->second);
        pending_.erase(it);
        if (!Fresh(request.created, now) || applied_.count(request.id)) return std::nullopt;
        applied_[request.id] = true;
        order_.push_back(request.id);
        if (order_.size() > 512) { applied_.erase(order_.front()); order_.pop_front(); }
        return request;
    }
    void Cancel(std::uint64_t token) {
        std::lock_guard<std::mutex> lock(mutex_);
        pending_.erase(token);
    }
private:
    std::mutex mutex_;
    std::uint64_t next_ = 0;
    std::map<std::uint64_t, Request> pending_;
    std::map<std::string, bool> applied_;
    std::deque<std::string> order_;
};
}
