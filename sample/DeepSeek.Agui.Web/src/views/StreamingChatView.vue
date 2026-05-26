<script setup lang="ts">
import { CopilotChat, CopilotChatConfigurationProvider, CopilotKitProvider } from "@copilotkit/vue";
import DemoToolRegistrations from "../components/DemoToolRegistrations.vue";
import ProtocolTracePanel from "../components/ProtocolTracePanel.vue";
import { getAguiEndpoint, useDemoRuntime } from "../lib/runtime";

const runtime = useDemoRuntime("streaming-chat");
</script>

<template>
  <CopilotKitProvider :agents__unsafe_dev_only="runtime.localAgents" :show-dev-console="false">
    <CopilotChatConfigurationProvider :agent-id="runtime.state.agentId" :thread-id="runtime.state.threadId"
      :has-explicit-thread-id="true">
      <DemoToolRegistrations />
      <main class="layout app-layout">
        <section class="panel chat-panel">
          <div class="panel-header">
            <div>
              <h2>AG-UI Chat</h2>
              <p class="panel-subtitle">Connected to {{ getAguiEndpoint() }}</p>
            </div>
            <button class="button" @click="runtime.reset()">Reset</button>
          </div>
          <div class="chat-surface">
            <CopilotChat class="copilot-chat-fill" :disable-system-message="true" :welcome-screen="false" />
          </div>
        </section>
        <ProtocolTracePanel title="Event Trace" :trace="runtime.state.trace" />
      </main>
    </CopilotChatConfigurationProvider>
  </CopilotKitProvider>
</template>
