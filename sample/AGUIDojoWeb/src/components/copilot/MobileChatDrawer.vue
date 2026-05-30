<script setup lang="ts">
import { CopilotChat } from "@copilotkit/vue/v2";
import type { AgentId } from "@/lib/agui";
import { useMobileChat } from "@/composables/useMobileChat";

const props = defineProps<{
  agentId: AgentId;
  title: string;
  description: string;
}>();

const defaultChatHeight = 50;
const {
  isChatOpen,
  setChatHeight,
  setIsChatOpen,
  isDragging,
  chatHeight,
  handleDragStart,
} = useMobileChat(defaultChatHeight);

function toggleChat() {
  if (!isChatOpen.value) {
    setChatHeight(defaultChatHeight);
  }
  setIsChatOpen(!isChatOpen.value);
}
</script>

<template>
  <div class="fixed bottom-0 left-0 right-0 z-50">
    <div class="h-6 bg-gradient-to-t from-white via-white to-transparent"></div>
    <div
      class="flex cursor-pointer items-center justify-between border-t border-gray-200 bg-white px-4 py-3 shadow-lg"
      @click="toggleChat"
    >
      <div>
        <div class="font-medium text-gray-900">{{ props.title }}</div>
        <div class="text-sm text-gray-500">{{ props.description }}</div>
      </div>
      <div class="transform transition-transform duration-300" :class="{ 'rotate-180': isChatOpen }">
        <svg class="h-6 w-6 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M5 15l7-7 7 7" />
        </svg>
      </div>
    </div>
  </div>

  <div
    class="fixed inset-x-0 bottom-0 z-40 flex flex-col rounded-t-2xl bg-white shadow-[0px_0px_20px_0px_rgba(0,0,0,0.15)] transition-all duration-300 ease-in-out"
    :class="[isChatOpen ? 'translate-y-0' : 'translate-y-full', isDragging ? 'transition-none' : '']"
    :style="{ height: `${chatHeight}vh`, paddingBottom: 'env(safe-area-inset-bottom)' }"
  >
    <div
      class="flex flex-shrink-0 cursor-grab justify-center pb-2 pt-3 active:cursor-grabbing"
      @mousedown="handleDragStart"
      @touchstart="handleDragStart"
    >
      <div class="h-1 w-12 rounded-full bg-gray-400 transition-colors hover:bg-gray-500"></div>
    </div>

    <div class="flex-shrink-0 border-b border-gray-100 px-4 py-3">
      <div class="flex items-center justify-between">
        <h3 class="font-semibold text-gray-900">{{ props.title }}</h3>
        <button
          class="rounded-full p-2 transition-colors hover:bg-gray-100"
          type="button"
          @click="setIsChatOpen(false)"
        >
          <svg class="h-5 w-5 text-gray-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12" />
          </svg>
        </button>
      </div>
    </div>

    <div class="flex min-h-0 flex-1 flex-col overflow-hidden pb-16">
      <CopilotChat :agent-id="props.agentId" class="flex h-full flex-col" />
    </div>
  </div>

  <div v-if="isChatOpen" class="fixed inset-0 z-30" @click="setIsChatOpen(false)" />
</template>
