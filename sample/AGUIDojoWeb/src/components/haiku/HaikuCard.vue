<script setup lang="ts">
import type { Haiku } from "@/lib/haiku";

defineProps<{
  haiku: Partial<Haiku>;
}>();
</script>

<template>
  <div
    data-testid="haiku-card"
    class="relative my-6 max-w-2xl overflow-hidden rounded-2xl border border-slate-200 bg-gradient-to-br from-slate-50 to-blue-50 p-8 dark:border-slate-700 dark:from-slate-900 dark:to-blue-950"
    :style="{ background: haiku.gradient }"
  >
    <div class="relative z-10 flex flex-col items-center space-y-6">
      <div
        v-for="(line, index) in haiku.japanese"
        :key="`${line}-${index}`"
        class="flex flex-col items-center space-y-2 text-center"
      >
        <p
          data-testid="haiku-japanese-line"
          class="bg-gradient-to-r from-slate-800 to-slate-600 bg-clip-text font-serif text-4xl font-bold tracking-wide text-transparent dark:from-slate-100 dark:to-slate-300 md:text-5xl"
        >
          {{ line }}
        </p>
        <p
          data-testid="haiku-english-line"
          class="max-w-md text-base font-light italic text-slate-600 dark:text-slate-400 md:text-lg"
        >
          {{ haiku.english?.[index] }}
        </p>
      </div>
    </div>

    <div
      v-if="haiku.image_name"
      class="relative z-10 mt-8 border-t border-slate-200 pt-8 dark:border-slate-700"
    >
      <div class="group relative overflow-hidden rounded-2xl shadow-xl">
        <img
          data-testid="haiku-image"
          class="h-64 w-full object-cover transition-transform duration-500 group-hover:scale-105 md:h-80"
          :src="`/images/${haiku.image_name}`"
          :alt="haiku.image_name"
        />
        <div
          class="absolute inset-0 bg-gradient-to-t from-black/20 to-transparent opacity-0 transition-opacity duration-300 group-hover:opacity-100"
        />
      </div>
    </div>
  </div>
</template>
