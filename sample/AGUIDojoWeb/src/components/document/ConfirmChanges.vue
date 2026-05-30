<script setup lang="ts">
import { ref } from "vue";

const props = defineProps<{
  status: string;
  respond?: (value: unknown) => Promise<void> | void;
  onReject: () => void;
  onConfirm: () => void;
}>();

const accepted = ref<boolean | null>(null);

function reject() {
  accepted.value = false;
  props.onReject();
  void props.respond?.({ accepted: false });
}

function confirm() {
  accepted.value = true;
  props.onConfirm();
  void props.respond?.({ accepted: true });
}
</script>

<template>
  <div
    data-testid="confirm-changes-modal"
    class="mb-5 mt-5 rounded border border-gray-200 bg-white p-6 shadow-lg"
  >
    <h2 class="mb-4 text-lg font-bold">Confirm Changes</h2>
    <p class="mb-6">Do you want to accept the changes?</p>
    <div v-if="accepted === null" class="flex justify-end space-x-4">
      <button
        data-testid="reject-button"
        class="rounded bg-gray-200 px-4 py-2 text-black disabled:opacity-50"
        :class="status === 'executing' ? 'cursor-pointer' : 'cursor-default'"
        :disabled="status !== 'executing'"
        type="button"
        @click="reject"
      >
        Reject
      </button>
      <button
        data-testid="confirm-button"
        class="rounded bg-black px-4 py-2 text-white disabled:opacity-50"
        :class="status === 'executing' ? 'cursor-pointer' : 'cursor-default'"
        :disabled="status !== 'executing'"
        type="button"
        @click="confirm"
      >
        Confirm
      </button>
    </div>
    <div v-else class="flex justify-end">
      <div data-testid="status-display" class="mt-4 inline-block rounded bg-gray-200 px-4 py-2 text-black">
        {{ accepted ? "✓ Accepted" : "✗ Rejected" }}
      </div>
    </div>
  </div>
</template>
