import { computed } from "vue";
import { useRoute } from "vue-router";

function asBoolean(value: unknown): boolean {
  if (Array.isArray(value)) {
    return asBoolean(value[0]);
  }
  return value === "1" || value === "true" || value === "open";
}

export function useUrlParams() {
  const route = useRoute();
  const chatDefaultOpen = computed(() => asBoolean(route.query.chat));

  return { chatDefaultOpen };
}
