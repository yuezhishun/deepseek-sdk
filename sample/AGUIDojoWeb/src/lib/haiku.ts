export interface Haiku {
  japanese: string[];
  english: string[];
  image_name: string | null;
  gradient: string;
}

export const VALID_IMAGE_NAMES = [
  "Osaka_Castle_Turret_Stone_Wall_Pine_Trees_Daytime.jpg",
  "Tokyo_Skyline_Night_Tokyo_Tower_Mount_Fuji_View.jpg",
  "Itsukushima_Shrine_Miyajima_Floating_Torii_Gate_Sunset_Long_Exposure.jpg",
  "Takachiho_Gorge_Waterfall_River_Lush_Greenery_Japan.jpg",
  "Bonsai_Tree_Potted_Japanese_Art_Green_Foliage.jpeg",
  "Shirakawa-go_Gassho-zukuri_Thatched_Roof_Village_Aerial_View.jpg",
  "Ginkaku-ji_Silver_Pavilion_Kyoto_Japanese_Garden_Pond_Reflection.jpg",
  "Senso-ji_Temple_Asakusa_Cherry_Blossoms_Kimono_Umbrella.jpg",
  "Cherry_Blossoms_Sakura_Night_View_City_Lights_Japan.jpg",
  "Mount_Fuji_Lake_Reflection_Cherry_Blossoms_Sakura_Spring.jpg",
];

export const PLACEHOLDER_HAIKU: Haiku = {
  japanese: ["仮の句よ", "まっさらながら", "花を呼ぶ"],
  english: ["A placeholder verse-", "even in a blank canvas,", "it beckons flowers."],
  image_name: null,
  gradient: "",
};

export function insertGeneratedHaiku(prev: Haiku[], next: Haiku): Haiku[] {
  return [next, ...prev.filter((haiku) => haiku.english[0] !== PLACEHOLDER_HAIKU.english[0])];
}
