import { onMounted, onUnmounted, shallowRef } from "vue";

export function useMobileView(breakpoint = 768) {
  const isMobile = shallowRef(false);

  const update = () => {
    isMobile.value = window.innerWidth < breakpoint;
  };

  onMounted(() => {
    update();
    window.addEventListener("resize", update);
  });

  onUnmounted(() => {
    window.removeEventListener("resize", update);
  });

  return { isMobile };
}
