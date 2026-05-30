export interface Step {
  description: string;
  status: "disabled" | "enabled" | "executing";
}
