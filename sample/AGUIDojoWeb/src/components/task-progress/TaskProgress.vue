<script setup lang="ts">
import { computed } from "vue";
import { Check, Clock } from "lucide-vue-next";
import { useTheme } from "@/composables/useTheme";

export interface TaskProgressStep {
  description: string;
  status: "pending" | "completed";
}

const props = defineProps<{
  steps: TaskProgressStep[];
}>();

const { theme } = useTheme();
const completedCount = computed(() =>
  props.steps.filter((step) => step.status === "completed").length,
);
const currentPendingIndex = computed(() =>
  props.steps.findIndex((step) => step.status === "pending"),
);
const isComplete = computed(() =>
  props.steps.length > 0 && completedCount.value === props.steps.length,
);
const progressPercentage = computed(() =>
  props.steps.length ? (completedCount.value / props.steps.length) * 100 : 0,
);
</script>

<template>
  <div class="flex w-full justify-center px-4">
    <div
      data-testid="task-progress"
      class="relative w-[700px] rounded-xl p-6 shadow-lg backdrop-blur-sm"
      :class="
        theme === 'dark'
          ? 'border border-slate-700/50 bg-gradient-to-br from-slate-900 via-slate-800 to-slate-900 text-white shadow-2xl'
          : 'border border-gray-200/80 bg-gradient-to-br from-white via-gray-50 to-white text-gray-800'
      "
    >
      <div class="mb-5">
        <div class="mb-3 flex items-center justify-between">
          <h3
            class="bg-gradient-to-r from-blue-600 to-purple-600 bg-clip-text text-xl font-bold text-transparent"
          >
            Task Progress
          </h3>
          <div class="text-sm" :class="theme === 'dark' ? 'text-slate-400' : 'text-gray-500'">
            {{ completedCount }}/{{ steps.length }} Complete
          </div>
        </div>
        <div
          class="relative h-2 overflow-hidden rounded-full"
          :class="theme === 'dark' ? 'bg-slate-700' : 'bg-gray-200'"
        >
          <div
            class="absolute left-0 top-0 h-full rounded-full bg-gradient-to-r from-blue-500 to-purple-500 transition-all duration-1000"
            :style="{ width: `${progressPercentage}%` }"
          />
        </div>
      </div>

      <div class="space-y-2">
        <div
          v-for="(step, index) in steps"
          :key="`${step.description}-${index}`"
          class="relative flex items-center rounded-lg p-2.5 transition-all duration-500"
          :class="
            step.status === 'completed'
              ? 'border border-green-200/60 bg-gradient-to-r from-green-50 to-emerald-50'
              : index === currentPendingIndex
                ? 'border border-blue-200/60 bg-gradient-to-r from-blue-50 to-purple-50 shadow-md shadow-blue-200/50'
                : 'border border-gray-200/60 bg-gray-50/50'
          "
        >
          <div
            class="mr-2 flex h-6 w-6 flex-shrink-0 items-center justify-center rounded-full"
            :class="
              step.status === 'completed'
                ? 'bg-gradient-to-br from-green-500 to-emerald-600'
                : 'bg-gradient-to-br from-blue-500 to-purple-600'
            "
          >
            <Check v-if="step.status === 'completed'" class="h-4 w-4 text-white" />
            <Clock v-else class="h-3 w-3 text-white" />
          </div>
          <div class="min-w-0 flex-1">
            <div
              data-testid="task-step-text"
              class="text-sm font-semibold transition-all"
              :class="step.status === 'completed' ? 'text-green-700' : 'text-blue-700'"
            >
              {{ step.description }}
            </div>
            <div
              v-if="step.status === 'pending' && index === currentPendingIndex && !isComplete"
              class="mt-1 animate-pulse text-sm text-blue-600"
            >
              Processing...
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
