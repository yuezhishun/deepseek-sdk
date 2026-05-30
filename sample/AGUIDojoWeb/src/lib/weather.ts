export interface WeatherToolResult {
  temperature: number;
  conditions: string;
  humidity: number;
  windSpeed: number;
  feelsLike: number;
}

export function parseToolResult(value: unknown): Record<string, any> {
  if (typeof value !== "string") {
    return (value ?? {}) as Record<string, any>;
  }

  try {
    return JSON.parse(value) as Record<string, any>;
  } catch {
    return {};
  }
}

export function normalizeWeatherResult(result: unknown): WeatherToolResult {
  const parsed = parseToolResult(result);

  return {
    temperature: parsed.temperature ?? 0,
    conditions: parsed.conditions ?? "clear",
    humidity: parsed.humidity ?? 0,
    windSpeed: parsed.wind_speed ?? parsed.windSpeed ?? 0,
    feelsLike: parsed.feels_like ?? parsed.feelsLike ?? parsed.temperature ?? 0,
  };
}

export function getThemeColor(conditions: string): string {
  const conditionLower = conditions.toLowerCase();
  if (conditionLower.includes("clear") || conditionLower.includes("sunny")) return "#667eea";
  if (conditionLower.includes("rain") || conditionLower.includes("storm")) return "#4A5568";
  if (conditionLower.includes("cloud")) return "#718096";
  if (conditionLower.includes("snow")) return "#63B3ED";
  return "#764ba2";
}
