<script setup lang="ts">
import { h, shallowRef } from "vue";
import { z } from "zod";
import { useAgentContext, useConfigureSuggestions, useFrontendTool, useRenderTool } from "@copilotkit/vue/v2";
import ChatPanel from "@/components/copilot/ChatPanel.vue";
import { AGENT_IDS } from "@/lib/agui";
import { parseToolResult } from "@/lib/weather";

const background = shallowRef("--copilot-kit-background-color");

useAgentContext({ description: "Name of the user", value: "Bob" });
useFrontendTool({
  name: "change_background",
  description:
    "Change the background color of the chat. Can be anything that the CSS background attribute accepts. Regular colors, linear of radial gradients etc.",
  parameters: z.object({
    background: z.string().describe("The background. Prefer gradients. Only use when asked."),
  }),
  handler: async ({ background: nextBackground }) => {
    background.value = nextBackground;
    return { status: "success", message: `Background changed to ${nextBackground}` };
  },
});
useRenderTool({
  name: "get_weather",
  parameters: z.object({ location: z.string() }),
  render: ({ parameters, result, status }: any) => {
    if (status !== "complete") return h("div", { "data-testid": "weather-info-loading" }, "Loading weather...");
    const parsed = parseToolResult(result);
    return h("div", { "data-testid": "weather-info" }, [
      h("strong", `Weather in ${parsed.city ?? parameters.location}`),
      h("div", `Temperature: ${parsed.temperature}°C`),
      h("div", `Humidity: ${parsed.humidity}%`),
      h("div", `Wind Speed: ${parsed.windSpeed ?? parsed.wind_speed} mph`),
      h("div", `Conditions: ${parsed.conditions}`),
    ]);
  },
});
useConfigureSuggestions({
  suggestions: [
    { title: "Change background", message: "Change the background to something new." },
    { title: "Generate sonnet", message: "Write a short sonnet about AI." },
  ],
  available: "always",
});
</script>

<template>
  <div
    data-testid="background-container"
    class="flex h-full min-h-0 w-full items-center justify-center"
    :style="{ background }"
  >
    <ChatPanel :agent-id="AGENT_IDS.agenticChat" />
  </div>
</template>
