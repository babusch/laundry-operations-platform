import i18n from "i18next";
import { initReactI18next } from "react-i18next";

import { resources, type SupportedLanguage } from "./resources";

export const languageStorageKey = "laundry.station.language";
export const supportedLanguages: readonly SupportedLanguage[] = ["en", "sv"];

function normalizeLanguage(language: string | null | undefined): SupportedLanguage | null {
  if (!language) return null;
  const baseLanguage = language.toLowerCase().split("-")[0];
  return supportedLanguages.find((candidate) => candidate === baseLanguage) ?? null;
}

export function detectInitialLanguage(
  storedLanguage: string | null,
  browserLanguages: readonly string[],
): SupportedLanguage {
  return (
    normalizeLanguage(storedLanguage) ??
    browserLanguages.map(normalizeLanguage).find((language) => language !== null) ??
    "en"
  );
}

function readStoredLanguage(): string | null {
  try {
    return window.localStorage.getItem(languageStorageKey);
  } catch {
    return null;
  }
}

export function persistLanguage(language: SupportedLanguage): void {
  try {
    window.localStorage.setItem(languageStorageKey, language);
  } catch {
    // A disabled or full browser store must not prevent station use.
  }
}

const initialLanguage = detectInitialLanguage(
  readStoredLanguage(),
  navigator.languages.length > 0 ? navigator.languages : [navigator.language],
);

void i18n.use(initReactI18next).init({
  resources,
  lng: initialLanguage,
  fallbackLng: "en",
  supportedLngs: [...supportedLanguages],
  interpolation: { escapeValue: false },
});

export { i18n, normalizeLanguage };
