<script setup lang="ts">
import { h, ref } from "vue";
import { z } from "zod";
import { useFrontendTool } from "@copilotkit/vue/v2";
import HaikuCard from "./HaikuCard.vue";
import {
  insertGeneratedHaiku,
  PLACEHOLDER_HAIKU,
  VALID_IMAGE_NAMES,
  type Haiku,
} from "@/lib/haiku";
import { AGENT_IDS } from "@/lib/agui";

const activeIndex = ref(0);
const haikus = ref<Haiku[]>([PLACEHOLDER_HAIKU]);

function addHaiku(haiku: Haiku) {
  haikus.value = insertGeneratedHaiku(haikus.value, haiku);
  activeIndex.value = 0;
}

useFrontendTool(
  {
    agentId: AGENT_IDS.toolBasedGenerativeUi,
    name: "generate_haiku",
    parameters: z.object({
      japanese: z.array(z.string()).describe("3 lines of haiku in Japanese"),
      english: z.array(z.string()).describe("3 lines of haiku translated to English"),
      image_name: z
        .string()
        .describe(`One relevant image name from: ${VALID_IMAGE_NAMES.join(", ")}`),
      gradient: z.string().describe("CSS Gradient color for the background"),
    }),
    followUp: false,
    handler: async ({ japanese, english, image_name, gradient }) => {
      addHaiku({
        japanese: japanese || [],
        english: english || [],
        image_name: image_name || null,
        gradient: gradient || "",
      });
      return "Haiku generated!";
    },
    render: ({ args }: any) => {
      if (!args.japanese) return null;
      return h(HaikuCard, { haiku: args as Haiku });
    },
  },
  [haikus],
);

defineExpose({ addHaiku, haikus });
</script>

<template>
  <div class="relative flex h-full w-full items-center justify-center">
    <div class="w-full max-w-4xl px-6 py-12 md:px-20" data-testid="haiku-carousel">
      <div
        v-for="(haiku, index) in haikus"
        v-show="index === activeIndex"
        :key="index"
        :data-testid="`carousel-item-${index}`"
        class="flex justify-center"
      >
        <HaikuCard :haiku="haiku" />
      </div>
      <div v-if="haikus.length > 1" class="mt-2 flex justify-center gap-3">
        <button
          class="rounded border border-slate-300 px-3 py-1 text-sm"
          type="button"
          @click="activeIndex = Math.max(0, activeIndex - 1)"
        >
          Previous
        </button>
        <button
          class="rounded border border-slate-300 px-3 py-1 text-sm"
          type="button"
          @click="activeIndex = Math.min(haikus.length - 1, activeIndex + 1)"
        >
          Next
        </button>
      </div>
    </div>
  </div>
</template>
