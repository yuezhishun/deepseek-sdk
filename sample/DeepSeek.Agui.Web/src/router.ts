import { createRouter, createWebHistory } from "vue-router";
import StreamingChatView from "./views/StreamingChatView.vue";

export default createRouter({
  history: createWebHistory(),
  routes: [
    { path: "/", component: StreamingChatView },
  ],
});
