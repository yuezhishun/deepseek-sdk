<script setup lang="ts">
import { computed, h, onBeforeUnmount, ref, shallowRef, watch } from "vue";
import { EditorContent, useEditor } from "@tiptap/vue-3";
import StarterKit from "@tiptap/starter-kit";
import { z } from "zod";
import {
  useAgent,
  useConfigureSuggestions,
  useHumanInTheLoop,
  UseAgentUpdate,
} from "@copilotkit/vue/v2";
import { AGENT_IDS } from "@/lib/agui";
import { diffPartialText, fromMarkdown } from "@/lib/document";
import ConfirmChanges from "./ConfirmChanges.vue";

interface AgentState {
  document: string;
}

const currentDocument = ref("");
const text = ref("");
const wasRunning = ref(false);

const editor = useEditor({
  extensions: [StarterKit],
  editorProps: {
    attributes: { class: "min-h-full p-10" },
  },
  onUpdate: ({ editor: nextEditor }) => {
    text.value = nextEditor.getText();
  },
});

const { agent } = useAgent({
  agentId: AGENT_IDS.predictiveStateUpdates,
  updates: [UseAgentUpdate.OnStateChanged, UseAgentUpdate.OnRunStatusChanged],
});

const agentState = computed(() => agent.value?.state as AgentState | undefined);
const isLoading = computed(() => Boolean(agent.value?.isRunning));
const placeholderVisible = computed(() => text.value.length === 0);

function setAgentState(state: AgentState) {
  agent.value?.setState(state);
}

function rejectChanges() {
  editor.value?.commands.setContent(fromMarkdown(currentDocument.value));
  setAgentState({ document: currentDocument.value });
}

function confirmChanges() {
  const nextDocument = agentState.value?.document || "";
  editor.value?.commands.setContent(fromMarkdown(nextDocument));
  currentDocument.value = nextDocument;
  setAgentState({ document: nextDocument });
}

useConfigureSuggestions({
  suggestions: [
    { title: "Write a pirate story", message: "Please write a story about a pirate named Candy Beard." },
    { title: "Write a mermaid story", message: "Please write a story about a mermaid named Luna." },
    { title: "Add character", message: "Please add a character named Courage." },
  ],
  available: "always",
});

watch(isLoading, (loading) => {
  if (loading) {
    currentDocument.value = editor.value?.getText() || "";
  }
  editor.value?.setEditable(!loading);

  if (wasRunning.value && !loading) {
    const nextDocument = agentState.value?.document || "";
    if (currentDocument.value.trim().length > 0 && currentDocument.value !== nextDocument) {
      editor.value?.commands.setContent(fromMarkdown(diffPartialText(currentDocument.value, nextDocument, true)));
    }
  }
  wasRunning.value = loading;
});

watch(
  () => agentState.value?.document,
  (document) => {
    if (!isLoading.value) return;
    const nextDocument = document || "";
    if (currentDocument.value.trim().length > 0) {
      editor.value?.commands.setContent(fromMarkdown(diffPartialText(currentDocument.value, nextDocument)));
    } else {
      editor.value?.commands.setContent(fromMarkdown(nextDocument));
    }
  },
);

watch(text, (value) => {
  if (!isLoading.value) {
    currentDocument.value = value;
    setAgentState({ document: value });
  }
});

useHumanInTheLoop(
  {
    agentId: AGENT_IDS.predictiveStateUpdates,
    name: "confirm_changes",
    render: ({ respond, status }: any) =>
      h(ConfirmChanges, {
        respond,
        status,
        onReject: rejectChanges,
        onConfirm: confirmChanges,
      }),
  },
  [shallowRef(agentState.value?.document)],
);

useHumanInTheLoop(
  {
    agentId: AGENT_IDS.predictiveStateUpdates,
    name: "write_document",
    description: "Present the proposed changes to the user for review",
    parameters: z.object({
      document: z.string().describe("The full updated document in markdown format"),
    }),
    render: ({ respond, status }: any) => {
      if (status !== "executing") return null;
      return h(ConfirmChanges, {
        respond,
        status,
        onReject: rejectChanges,
        onConfirm: confirmChanges,
      });
    },
  },
  [shallowRef(agentState.value?.document)],
);

onBeforeUnmount(() => {
  editor.value?.destroy();
});
</script>

<template>
  <div class="relative h-full min-h-0 w-full overflow-auto">
    <div v-if="placeholderVisible" class="pointer-events-none absolute left-6 top-6 m-4 text-gray-400">
      Write whatever you want here in Markdown format...
    </div>
    <EditorContent :editor="editor" />
  </div>
</template>
