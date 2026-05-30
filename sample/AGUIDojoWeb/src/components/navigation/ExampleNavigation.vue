<script setup lang="ts">
import { computed } from "vue";
import { RouterLink, useRoute } from "vue-router";
import { demoRoutes } from "@/router";

const route = useRoute();

const currentPath = computed(() => route.path);
</script>

<template>
  <nav class="example-nav" aria-label="Example navigation">
    <div class="example-nav__intro">
      <h1 class="example-nav__eyebrow">AG UI Examples</h1>
      <!-- <h1 class="example-nav__title">Examples</h1>
      <p class="example-nav__description">在任意示例中都可以直接切换到其他示例。</p> -->
    </div>

    <ul class="example-nav__list">
      <li v-for="item in demoRoutes" :key="item.path" class="example-nav__item">
        <RouterLink :to="item.path" class="example-nav__link"
          :class="{ 'example-nav__link--active': currentPath === item.path }">
          <span class="example-nav__link-title">{{ item.title }}</span>
          <span class="example-nav__link-description">{{ item.description }}</span>
        </RouterLink>
      </li>
    </ul>
  </nav>
</template>

<style scoped>
.example-nav {
  display: flex;
  height: 100%;
  min-height: 0;
  flex-direction: column;
  gap: 0.25rem;
}

.example-nav__intro {
  display: flex;
  flex-shrink: 0;
  flex-direction: column;
  padding: 1rem 1rem;
  gap: 0.75rem;
}

.example-nav__eyebrow {
  margin: 0;
  /* font-size: 1.72rem; */
  font-weight: 700;
  letter-spacing: 0.18em;
  text-transform: uppercase;
  color: #9a3412;
}

.example-nav__title {
  margin: 0;
  font-size: clamp(2rem, 2.4vw, 2.75rem);
  line-height: 1;
  color: #111827;
}

.example-nav__description {
  margin: 0;
  max-width: 28ch;
  font-size: 0.98rem;
  line-height: 1.65;
  color: #4b5563;
}

.example-nav__list {
  display: flex;
  flex: 1;
  min-height: 0;
  flex-direction: column;
  gap: 0.25rem;
  margin: 0;
  overflow-y: auto;
  padding: 0 0.5rem 0 0;
  list-style: none;
  scrollbar-gutter: stable;
}

.example-nav__item {
  display: block;
}

.example-nav__link {
  display: flex;
  flex-direction: column;
  gap: 0.45rem;
  padding: 1rem 1rem;
  border: 1px solid rgba(148, 163, 184, 0.28);
  border-radius: 5px;
  background:
    linear-gradient(180deg, rgba(255, 255, 255, 0.96), rgba(255, 247, 237, 0.84));
  box-shadow: 0 18px 40px rgba(15, 23, 42, 0.07);
  text-decoration: none;
  transition:
    transform 160ms ease,
    border-color 160ms ease,
    box-shadow 160ms ease;
}

.example-nav__link:hover {
  transform: translateX(4px);
  border-color: rgba(234, 88, 12, 0.45);
  box-shadow: 0 20px 45px rgba(234, 88, 12, 0.12);
}

.example-nav__link--active {
  border-color: rgba(194, 65, 12, 0.7);
  background:
    linear-gradient(180deg, rgba(255, 237, 213, 0.98), rgba(255, 251, 235, 0.95));
  box-shadow: 0 24px 48px rgba(194, 65, 12, 0.16);
}

.example-nav__link-title {
  font-size: 1rem;
  font-weight: 700;
  line-height: 1.35;
  color: #111827;
}

.example-nav__link-description {
  font-size: 0.88rem;
  line-height: 1.55;
  color: #4b5563;
}

@media (max-width: 960px) {
  .example-nav {
    gap: 1.5rem;
  }

  .example-nav__description {
    max-width: none;
  }

  .example-nav__list {
    overflow-x: auto;
    overflow-y: hidden;
    flex-direction: row;
    padding: 0 0 0.5rem;
    scrollbar-gutter: auto;
  }

  .example-nav__item {
    min-width: min(18rem, calc(100vw - 3rem));
    flex: 0 0 auto;
  }

  .example-nav__link:hover {
    transform: translateY(-2px);
  }
}
</style>
