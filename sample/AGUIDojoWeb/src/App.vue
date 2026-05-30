<script setup lang="ts">
import { RouterView } from "vue-router";
import ExampleNavigation from "@/components/navigation/ExampleNavigation.vue";
</script>

<template>
  <div class="app-shell">
    <aside class="app-shell__sidebar">
      <ExampleNavigation />
    </aside>
    <main class="app-shell__content">
      <RouterView />
    </main>
  </div>
</template>

<style scoped>
.app-shell {
  display: flex;
  height: 100dvh;
  width: 100%;
  overflow: hidden;
  background:
    radial-gradient(circle at top left, rgba(254, 215, 170, 0.34), transparent 30%),
    linear-gradient(135deg, #fffaf2 0%, #fff 42%, #f8fafc 100%);
}

.app-shell__sidebar {
  width: 380px;
  min-width: 380px;
  min-height: 0;
  padding: 0.2rem 0.5rem;
  border-right: 1px solid rgba(148, 163, 184, 0.22);
  background:
    linear-gradient(180deg, rgba(255, 251, 235, 0.92), rgba(255, 255, 255, 0.72));
  backdrop-filter: blur(18px);
}

.app-shell__content {
  min-width: 0;
  flex: 1;
  min-height: 0;
  overflow: hidden;
}

@media (max-width: 960px) {
  .app-shell {
    flex-direction: column;
  }

  .app-shell__sidebar {
    width: auto;
    min-width: 0;
    padding: 1.25rem 1rem;
    border-right: 0;
    border-bottom: 1px solid rgba(148, 163, 184, 0.22);
  }
}
</style>
