import { onMounted, onUnmounted, shallowRef } from "vue";

export function useTheme() {
  const theme = shallowRef<"light" | "dark">("light");

  const update = () => {
    theme.value = document.documentElement.classList.contains("dark") ? "dark" : "light";
  };

  onMounted(() => {
    update();
    const observer = new MutationObserver(update);
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ["class"] });
    onUnmounted(() => observer.disconnect());
  });

  return { theme };
}
