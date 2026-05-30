<script setup lang="ts">
import { computed } from "vue";
import { Cloud, CloudRain, Sun } from "lucide-vue-next";
import type { WeatherToolResult } from "@/lib/weather";

const props = defineProps<{
  location?: string;
  themeColor: string;
  result: WeatherToolResult;
}>();

const icon = computed(() => {
  const conditions = props.result.conditions.toLowerCase();
  if (conditions.includes("clear") || conditions.includes("sunny")) return Sun;
  if (
    conditions.includes("rain") ||
    conditions.includes("drizzle") ||
    conditions.includes("snow") ||
    conditions.includes("thunderstorm")
  ) {
    return CloudRain;
  }
  return Cloud;
});
</script>

<template>
  <div
    data-testid="weather-card"
    class="my-4 mt-6 w-full max-w-md overflow-hidden rounded-xl"
    :style="{ backgroundColor: themeColor }"
  >
    <div class="w-full bg-white/20 p-4">
      <div class="flex items-center justify-between">
        <div>
          <h3 data-testid="weather-city" class="text-xl font-bold capitalize text-white">
            {{ location }}
          </h3>
          <p class="text-white">Current Weather</p>
        </div>
        <component :is="icon" class="h-14 w-14 text-yellow-200" />
      </div>

      <div class="mt-4 flex items-end justify-between">
        <div class="text-3xl font-bold text-white">
          <span>{{ result.temperature }}° C</span>
          <span class="text-sm text-white/50">
            / {{ ((result.temperature * 9) / 5 + 32).toFixed(1) }}° F
          </span>
        </div>
        <div class="text-sm capitalize text-white">{{ result.conditions }}</div>
      </div>

      <div class="mt-4 border-t border-white pt-4">
        <div class="grid grid-cols-3 gap-2 text-center">
          <div data-testid="weather-humidity">
            <p class="text-xs text-white">Humidity</p>
            <p class="font-medium text-white">{{ result.humidity }}%</p>
          </div>
          <div data-testid="weather-wind">
            <p class="text-xs text-white">Wind</p>
            <p class="font-medium text-white">{{ result.windSpeed }} mph</p>
          </div>
          <div data-testid="weather-feels-like">
            <p class="text-xs text-white">Feels Like</p>
            <p class="font-medium text-white">{{ result.feelsLike }}°</p>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
