<script setup lang="ts">
defineProps<{ cards: any[] }>();
</script>

<template>
  <section class="card-stack">
    <article v-for="card in cards" :key="card.id" class="tool-card">
      <div class="tool-title">{{ card.name }}</div>
      <div class="tool-meta">{{ card.neighborhood }} · {{ card.layout }} · ${{ card.monthlyRent }}</div>
      <div class="tool-meta">Available {{ card.availableFrom }}</div>
      <div class="chip-row">
        <span v-for="tag in card.tags" :key="tag" class="chip">{{ tag }}</span>
      </div>
    </article>
  </section>
</template>
