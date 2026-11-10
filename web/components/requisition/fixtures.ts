const FIXTURES_KEY = "testlab.fixtures";

export interface SavedFixture {
  name: string;
  savedAt: string;
  raw: string;
}

const CHANGE_EVENT = "testlab-fixtures-changed";

// A string snapshot is stable, which useSyncExternalStore needs.
export function readFixturesText(): string {
  try {
    return window.localStorage.getItem(FIXTURES_KEY) ?? "";
  } catch {
    return "";
  }
}

export function subscribeFixtures(onChange: () => void): () => void {
  window.addEventListener("storage", onChange);
  window.addEventListener(CHANGE_EVENT, onChange);
  return () => {
    window.removeEventListener("storage", onChange);
    window.removeEventListener(CHANGE_EVENT, onChange);
  };
}

export function parseFixtures(text: string): SavedFixture[] {
  try {
    if (!text) return [];
    const parsed: unknown = JSON.parse(text);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter(
      (f): f is SavedFixture => !!f && typeof f === "object" && typeof (f as SavedFixture).name === "string" && typeof (f as SavedFixture).raw === "string",
    );
  } catch {
    return [];
  }
}

export function storeFixtures(list: SavedFixture[]): boolean {
  try {
    window.localStorage.setItem(FIXTURES_KEY, JSON.stringify(list.slice(0, 100)));
    window.dispatchEvent(new Event(CHANGE_EVENT));
    return true;
  } catch {
    return false;
  }
}
