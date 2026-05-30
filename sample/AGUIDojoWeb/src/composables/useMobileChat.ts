import { onUnmounted, shallowRef } from "vue";

export function useMobileChat(defaultHeight = 50) {
  const isChatOpen = shallowRef(false);
  const chatHeight = shallowRef(defaultHeight);
  const isDragging = shallowRef(false);

  let startY = 0;
  let startHeight = defaultHeight;

  const handleDragMove = (event: MouseEvent | TouchEvent) => {
    if (!isDragging.value) return;
    const clientY = "touches" in event ? event.touches[0]?.clientY : event.clientY;
    if (clientY == null) return;

    const deltaY = startY - clientY;
    const deltaVh = (deltaY / window.innerHeight) * 100;
    chatHeight.value = Math.min(90, Math.max(30, startHeight + deltaVh));
  };

  const handleDragEnd = () => {
    isDragging.value = false;
    window.removeEventListener("mousemove", handleDragMove);
    window.removeEventListener("mouseup", handleDragEnd);
    window.removeEventListener("touchmove", handleDragMove);
    window.removeEventListener("touchend", handleDragEnd);
  };

  const handleDragStart = (event: MouseEvent | TouchEvent) => {
    isDragging.value = true;
    startY = "touches" in event ? (event.touches[0]?.clientY ?? 0) : event.clientY;
    startHeight = chatHeight.value;
    window.addEventListener("mousemove", handleDragMove);
    window.addEventListener("mouseup", handleDragEnd);
    window.addEventListener("touchmove", handleDragMove);
    window.addEventListener("touchend", handleDragEnd);
  };

  onUnmounted(handleDragEnd);

  return {
    isChatOpen,
    chatHeight,
    isDragging,
    setIsChatOpen: (value: boolean) => {
      isChatOpen.value = value;
    },
    setChatHeight: (value: number) => {
      chatHeight.value = value;
    },
    handleDragStart,
  };
}
