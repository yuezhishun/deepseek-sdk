<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { useTheme } from "@/composables/useTheme";
import type { Step } from "./types";

const props = defineProps<{
  steps: Step[];
  status?: string;
  mode: "perform" | "confirm";
  respond?: (value: unknown) => Promise<void> | void;
}>();

const localSteps = ref<Step[]>([]);
const accepted = ref<boolean | null>(null);
const { theme } = useTheme();

watch(
  () => props.steps,
  (steps) => {
    localSteps.value = steps.map((step) => ({ ...step }));
  },
  { immediate: true, deep: true },
);

const enabledCount = computed(() =>
  localSteps.value.filter((step) => step.status === "enabled").length,
);

function toggleStep(index: number) {
  if (props.mode === "confirm" && props.status !== "executing") return;
  localSteps.value[index] = {
    ...localSteps.value[index],
    status: localSteps.value[index].status === "enabled" ? "disabled" : "enabled",
  };
}

function performSteps() {
  const selectedSteps = localSteps.value
    .filter((step) => step.status === "enabled")
    .map((step) => step.description);
  void props.respond?.(`The user selected the following steps: ${selectedSteps.join(", ")}`);
}

function reject() {
  accepted.value = false;
  void props.respond?.({ accepted: false });
}

function confirm() {
  accepted.value = true;
  void props.respond?.({
    accepted: true,
    steps: localSteps.value.filter((step) => step.status === "enabled"),
  });
}
</script>

<template>
  <div data-testid="select-steps" class="flex">
    <div
      class="relative w-[600px] rounded-xl p-6 shadow-lg backdrop-blur-sm"
      :class="
        theme === 'dark'
          ? 'border border-slate-700/50 bg-gradient-to-br from-slate-900 via-slate-800 to-slate-900 text-white shadow-2xl'
          : 'border border-gray-200/80 bg-gradient-to-br from-white via-gray-50 to-white text-gray-800'
      "
    >
      <div class="mb-5">
        <div class="mb-3 flex items-center justify-between">
          <h2
            class="bg-gradient-to-r from-blue-600 to-purple-600 bg-clip-text text-xl font-bold text-transparent"
          >
            Select Steps
          </h2>
          <div class="flex items-center gap-3">
            <div class="text-sm" :class="theme === 'dark' ? 'text-slate-400' : 'text-gray-500'">
              {{ enabledCount }}/{{ localSteps.length }} Selected
            </div>
            <div
              v-if="mode === 'confirm'"
              class="rounded-full px-2 py-1 text-xs font-medium"
              :class="
                status === 'executing'
                  ? 'border border-blue-200 bg-blue-50 text-blue-600'
                  : 'bg-gray-100 text-gray-600'
              "
            >
              {{ status === "executing" ? "Ready" : "Waiting" }}
            </div>
          </div>
        </div>
        <div
          class="relative h-2 overflow-hidden rounded-full"
          :class="theme === 'dark' ? 'bg-slate-700' : 'bg-gray-200'"
        >
          <div
            class="absolute left-0 top-0 h-full rounded-full bg-gradient-to-r from-blue-500 to-purple-500 transition-all duration-500"
            :style="{ width: `${localSteps.length ? (enabledCount / localSteps.length) * 100 : 0}%` }"
          />
        </div>
      </div>

      <div class="mb-6 space-y-3">
        <div
          v-for="(step, index) in localSteps"
          :key="`${step.description}-${index}`"
          class="flex items-center rounded-lg p-3 transition-all duration-300"
          :class="
            step.status === 'enabled'
              ? 'border border-blue-200/60 bg-gradient-to-r from-blue-50 to-purple-50'
              : 'border border-gray-200/40 bg-gray-50/50'
          "
        >
          <label data-testid="step-item" class="flex w-full cursor-pointer items-center">
            <span class="relative">
              <input
                class="sr-only"
                type="checkbox"
                :checked="step.status === 'enabled'"
                :disabled="mode === 'confirm' && status !== 'executing'"
                @change="toggleStep(index)"
              />
              <span
                class="flex h-5 w-5 items-center justify-center rounded border-2 transition-all"
                :class="
                  step.status === 'enabled'
                    ? 'border-blue-500 bg-gradient-to-br from-blue-500 to-purple-600'
                    : 'border-gray-300 bg-white'
                "
              >
                <span v-if="step.status === 'enabled'" class="text-xs text-white">✓</span>
              </span>
            </span>
            <span
              data-testid="step-text"
              class="ml-3 font-medium transition-all"
              :class="
                step.status !== 'enabled' && status !== 'inProgress'
                  ? 'text-gray-400 line-through'
                  : 'text-gray-800'
              "
            >
              {{ step.description }}
            </span>
          </label>
        </div>
      </div>

      <div v-if="mode === 'perform'" class="flex justify-center">
        <button
          class="rounded-lg bg-gradient-to-r from-purple-500 to-purple-700 px-6 py-3 font-semibold text-white shadow-lg transition-all hover:scale-105"
          type="button"
          @click="performSteps"
        >
          Perform Steps
          <span class="ml-1 rounded-full bg-purple-600/20 px-2 py-1 text-xs font-bold">
            {{ enabledCount }}
          </span>
        </button>
      </div>

      <div v-if="mode === 'confirm' && accepted === null" class="flex justify-center gap-4">
        <button
          class="rounded-lg border border-gray-300 bg-gray-100 px-6 py-3 font-semibold text-gray-800 disabled:cursor-not-allowed disabled:opacity-50"
          type="button"
          :disabled="status !== 'executing'"
          @click="reject"
        >
          Reject
        </button>
        <button
          class="rounded-lg bg-gradient-to-r from-green-500 to-emerald-600 px-6 py-3 font-semibold text-white disabled:cursor-not-allowed disabled:opacity-50"
          type="button"
          :disabled="status !== 'executing'"
          @click="confirm"
        >
          Confirm
          <span class="ml-2 rounded-full bg-green-600/20 px-2 py-1 text-xs font-bold">
            {{ enabledCount }}
          </span>
        </button>
      </div>

      <div v-if="mode === 'confirm' && accepted !== null" class="flex justify-center">
        <div
          class="rounded-lg border px-6 py-3 font-semibold"
          :class="
            accepted
              ? 'border-green-200 bg-green-50 text-green-700'
              : 'border-red-200 bg-red-50 text-red-700'
          "
        >
          {{ accepted ? "✓ Accepted" : "✗ Rejected" }}
        </div>
      </div>
    </div>
  </div>
</template>
