#define IMGUI_DISABLE_INCLUDE_IMCONFIG_H
#define ImTextureID ImU64
#include <imgui.h>
#include <reshade.hpp>

#include <atomic>
#include <cstdint>

namespace
{
    std::atomic<std::uint64_t> g_present_count{ 0 };
    std::atomic_flag g_present_logged = ATOMIC_FLAG_INIT;
    std::atomic_flag g_overlay_logged = ATOMIC_FLAG_INIT;
    float g_input_test = 50.0f;
    bool g_interaction_confirmed = false;
    bool g_addon_registered = false;

    void on_present(reshade::api::effect_runtime *)
    {
        g_present_count.fetch_add(1, std::memory_order_relaxed);

        if (!g_present_logged.test_and_set(std::memory_order_relaxed))
            reshade::log::message(reshade::log::level::info, "NVIDIA Game Filter Hook received its first present callback.");
    }

    void draw_game_filter_overlay(reshade::api::effect_runtime *)
    {
        if (!g_overlay_logged.test_and_set(std::memory_order_relaxed))
            reshade::log::message(reshade::log::level::info, "NVIDIA Game Filter overlay panel is rendering.");

        ImGui::TextColored(ImVec4(0.35f, 0.78f, 0.48f, 1.0f), "NVIDIA");
        ImGui::SameLine();
        ImGui::TextUnformatted("GAME FILTER");
        ImGui::Separator();

        ImGui::TextColored(ImVec4(0.30f, 0.88f, 0.60f, 1.0f), "HOOK STATUS: ACTIVE");
        ImGui::Text("ReShade Add-on API: %d", RESHADE_API_VERSION);
        ImGui::Text("Present callbacks: %llu",
            static_cast<unsigned long long>(g_present_count.load(std::memory_order_relaxed)));

        ImGui::Spacing();
        ImGui::TextWrapped(
            "This panel is rendered inside the game's ReShade overlay. "
            "It confirms that the add-on loaded and receives frame callbacks.");

        ImGui::Spacing();
        ImGui::Separator();
        ImGui::TextUnformatted("Input-path test (not a filter control)");
        ImGui::SliderFloat("UI input test", &g_input_test, 0.0f, 100.0f, "%.0f");

        if (ImGui::Button("Test interaction"))
            g_interaction_confirmed = !g_interaction_confirmed;

        if (g_interaction_confirmed)
            ImGui::TextColored(ImVec4(0.30f, 0.88f, 0.60f, 1.0f), "Overlay input is working.");

        ImGui::Spacing();
        ImGui::TextDisabled("v0.1 - filter effects are not connected yet.");
    }

    bool register_hook(HMODULE module)
    {
        if (!reshade::register_addon(module))
            return false;

        g_addon_registered = true;
        reshade::register_event<reshade::addon_event::reshade_present>(&on_present);
        reshade::register_overlay("NVIDIA Game Filter", &draw_game_filter_overlay);
        reshade::log::message(reshade::log::level::info, "NVIDIA Game Filter Hook add-on registered.");
        return true;
    }

    void unregister_hook(HMODULE module)
    {
        if (!g_addon_registered)
            return;

        reshade::unregister_overlay("NVIDIA Game Filter", &draw_game_filter_overlay);
        reshade::unregister_event<reshade::addon_event::reshade_present>(&on_present);
        reshade::unregister_addon(module);
        g_addon_registered = false;
    }
}

extern "C" __declspec(dllexport) const char *NAME = "NVIDIA Game Filter Hook";
extern "C" __declspec(dllexport) const char *DESCRIPTION =
    "Proof-of-concept in-game overlay using the ReShade add-on API.";

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(module);
        if (!register_hook(module))
            return FALSE;
        break;

    case DLL_PROCESS_DETACH:
        unregister_hook(module);
        break;
    }

    return TRUE;
}
