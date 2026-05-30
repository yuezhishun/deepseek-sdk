<script setup lang="ts">
import { h } from "vue";
import { z } from "zod";
import { useConfigureSuggestions, useRenderTool } from "@copilotkit/vue/v2";
import ChatPanel from "@/components/copilot/ChatPanel.vue";
import WeatherCard from "@/components/weather/WeatherCard.vue";
import { AGENT_IDS } from "@/lib/agui";
import { getThemeColor, normalizeWeatherResult } from "@/lib/weather";

useRenderTool({
  name: "get_weather",
  parameters: z.object({ location: z.string() }),
  render: ({ parameters, result, status }: any) => {
    if (status !== "complete") {
      return h("div", { class: "max-w-md rounded-lg bg-[#667eea] p-4 text-white" }, "⚙️ Retrieving weather...");
    }
    const weatherResult = normalizeWeatherResult(result);
    return h(WeatherCard, {
      location: parameters.location,
      themeColor: getThemeColor(weatherResult.conditions),
      result: weatherResult,
    });
  },
});
useConfigureSuggestions({
  suggestions: [
    { title: "Weather in San Francisco", message: "What's the weather like in San Francisco?" },
    { title: "Weather in New York", message: "Tell me about the weather in New York." },
    { title: "Weather in Tokyo", message: "How's the weather in Tokyo today?" },
  ],
  available: "always",
});
</script>

<template>
  <div class="h-full min-h-0 w-full">
    <ChatPanel :agent-id="AGENT_IDS.backendToolRendering" />
  </div>
</template>
