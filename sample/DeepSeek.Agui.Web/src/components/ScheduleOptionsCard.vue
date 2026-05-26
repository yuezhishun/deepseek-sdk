<script setup lang="ts">
defineProps<{ payload: any }>();
</script>

<template>
  <section class="panel">
    <div class="eyebrow">Schedule Options</div>
    <ul class="plain-list">
      <li v-for="slot in payload.slots" :key="slot.id">{{ slot.id }} · {{ slot.start }} · {{ slot.agentName }}</li>
    </ul>
  </section>
</template>
