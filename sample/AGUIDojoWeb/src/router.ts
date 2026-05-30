import type { Component } from "vue";
import { createRouter, createWebHistory } from "vue-router";
import type { RouteRecordRaw } from "vue-router";
import AgenticChatView from "./views/AgenticChatView.vue";
import AgenticGenerativeUiView from "./views/AgenticGenerativeUiView.vue";
import BackendToolRenderingView from "./views/BackendToolRenderingView.vue";
import HumanInTheLoopView from "./views/HumanInTheLoopView.vue";
import PredictiveStateUpdatesView from "./views/PredictiveStateUpdatesView.vue";
import SharedStateView from "./views/SharedStateView.vue";
import ToolBasedGenerativeUiView from "./views/ToolBasedGenerativeUiView.vue";

export interface DemoRouteDefinition {
  path: string;
  title: string;
  description: string;
  component: Component;
}

export const demoRoutes: DemoRouteDefinition[] = [
  {
    path: "/agentic_chat",
    title: "Agentic Chat",
    description: "基础智能体聊天，支持前端工具和建议提示。",
    component: AgenticChatView,
  },
  {
    path: "/backend_tool_rendering",
    title: "Backend Tool Rendering",
    description: "后端工具返回结构化结果，由前端进行界面渲染。",
    component: BackendToolRenderingView,
  },
  {
    path: "/human_in_the_loop",
    title: "Human In The Loop",
    description: "展示需要用户确认、批准和继续执行的交互流程。",
    component: HumanInTheLoopView,
  },
  {
    path: "/tool_based_generative_ui",
    title: "Tool Based Generative UI",
    description: "通过工具调用驱动动态 UI 生成与状态回传。",
    component: ToolBasedGenerativeUiView,
  },
  {
    path: "/agentic_generative_ui",
    title: "Agentic Generative UI",
    description: "将智能体对话和生成式界面编排在同一工作流中。",
    component: AgenticGenerativeUiView,
  },
  {
    path: "/shared_state",
    title: "Shared State",
    description: "多个交互面板共享同一份上下文和状态。",
    component: SharedStateView,
  },
  {
    path: "/predictive_state_updates",
    title: "Predictive State Updates",
    description: "在最终结果返回前，先展示预测性状态更新。",
    component: PredictiveStateUpdatesView,
  },
];

export const routes: RouteRecordRaw[] = [
  { path: "/", redirect: demoRoutes[0].path },
  ...demoRoutes.map(({ path, component, title, description }) => ({
    path,
    component,
    meta: {
      title,
      description,
    },
  })),
];

export const router = createRouter({
  history: createWebHistory(),
  routes,
});
