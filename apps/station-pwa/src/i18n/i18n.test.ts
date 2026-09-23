import { describe, expect, it } from "vitest";

import { detectInitialLanguage } from "./i18n";
import { resources } from "./resources";

function translationKeys(value: object, prefix = ""): string[] {
  return Object.entries(value).flatMap(([key, child]) => {
    const path = prefix ? `${prefix}.${key}` : key;
    return typeof child === "object" && child !== null
      ? translationKeys(child, path)
      : [path];
  });
}

describe("station language detection", () => {
  it("prefers the saved supported language", () => {
    expect(detectInitialLanguage("en", ["sv-SE"])).toBe("en");
  });

  it("uses a supported browser language including regional variants", () => {
    expect(detectInitialLanguage(null, ["de-DE", "sv-SE", "en-GB"])).toBe("sv");
  });

  it("falls back to English for unsupported languages", () => {
    expect(detectInitialLanguage("de", ["fr-FR"])).toBe("en");
  });

  it("keeps English and Swedish translation keys aligned", () => {
    expect(translationKeys(resources.sv.translation).sort()).toEqual(
      translationKeys(resources.en.translation).sort(),
    );
  });
});
