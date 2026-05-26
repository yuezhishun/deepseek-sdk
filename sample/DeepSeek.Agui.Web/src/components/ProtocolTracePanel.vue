<script setup lang="ts">
import type { TraceRecord } from "../lib/types";

defineProps<{
  title: string;
  trace: TraceRecord[];
}>();
</script>

<template>
  <section class="panel trace-panel">
    <div class="panel-header">
      <div>
        <div class="eyebrow">Trace</div>
        <h2>{{ title }}</h2>
      </div>
      <div class="pill">{{ trace.length }} events</div>
    </div>
    <div class="trace-list">
      <div v-if="trace.length === 0" class="empty">No events yet.</div>
      <details v-for="item in trace" :key="item.id" class="trace-item">
        <summary>
          <span class="trace-type">{{ item.type }}</span>
          <span>{{ item.summary }}</span>
        </summary>
        <pre>{{ JSON.stringify(item.payload, null, 2) }}</pre>
      </details>
    </div>
  </section>
</template>
