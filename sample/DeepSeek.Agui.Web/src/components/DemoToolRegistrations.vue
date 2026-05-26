<script setup lang="ts">
import { h, markRaw } from "vue";
import { z } from "zod";
import { useRenderTool } from "@copilotkit/vue/v2";
import ApartmentCardsTool from "./ApartmentCardsTool.vue";
import ScheduleOptionsCard from "./ScheduleOptionsCard.vue";

useRenderTool(
  {
    name: "render_apartment_cards",
    parameters: z.object({ apartments: z.array(z.any()) }),
    render: (props: any) => h(markRaw(ApartmentCardsTool as any), { cards: props.parameters?.apartments ?? [] }),
  } as any,
  [],
);

useRenderTool(
  {
    name: "render_schedule_options",
    parameters: z.object({ slots: z.array(z.any()) }),
    render: (props: any) => h(markRaw(ScheduleOptionsCard as any), { payload: { slots: props.parameters?.slots ?? [] } }),
  } as any,
  [],
);
</script>

<template>
  <div style="display: none" />
</template>
