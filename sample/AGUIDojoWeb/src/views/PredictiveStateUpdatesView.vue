<script setup lang="ts">
import CopilotRuntime from "@/components/copilot/CopilotRuntime.vue";
import MobileChatDrawer from "@/components/copilot/MobileChatDrawer.vue";
import SidebarPanel from "@/components/copilot/SidebarPanel.vue";
import DocumentEditor from "@/components/document/DocumentEditor.vue";
import { useMobileView } from "@/composables/useMobileView";
import { useUrlParams } from "@/composables/useUrlParams";
import { AGENT_IDS } from "@/lib/agui";

const { isMobile } = useMobileView();
const { chatDefaultOpen } = useUrlParams();
</script>

<template>
  <CopilotRuntime :agent-id="AGENT_IDS.predictiveStateUpdates">
    <div class="h-full min-h-0 w-full overflow-hidden">
      <MobileChatDrawer
        v-if="isMobile"
        :agent-id="AGENT_IDS.predictiveStateUpdates"
        title="AI Document Editor"
        description="Ask me to create or edit a document"
      />
      <SidebarPanel
        v-else
        :agent-id="AGENT_IDS.predictiveStateUpdates"
        :default-open="chatDefaultOpen"
        title="AI Document Editor"
      />
      <DocumentEditor />
    </div>
  </CopilotRuntime>
</template>
