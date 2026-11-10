"use client";

import { useEffect, useRef } from "react";

const UNSAVED_MESSAGE = "You have unsaved changes. Leave this page and discard them?";

type GuardState = Record<string, unknown> & { __testlabGuard?: boolean };

// Covers closing the tab, in-app links (a capture listener runs before Next's Link handler) and the Back button
// (a guard history entry, pushed again if the user stays).
export function useUnsavedChangesGuard(dirty: boolean, message = UNSAVED_MESSAGE) {
  const dirtyRef = useRef(dirty);
  const ignoreNextPop = useRef(false);

  useEffect(() => {
    dirtyRef.current = dirty;
  }, [dirty]);

  useEffect(() => {
    const onBeforeUnload = (e: BeforeUnloadEvent) => {
      if (!dirtyRef.current) return;
      e.preventDefault();
      e.returnValue = message;
      return message;
    };
    const onClick = (e: MouseEvent) => {
      if (!dirtyRef.current || e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
      const anchor = (e.target as Element | null)?.closest?.("a[href]") as HTMLAnchorElement | null;
      if (!anchor || anchor.target === "_blank" || anchor.hasAttribute("download")) return;
      const url = new URL(anchor.href, window.location.href);
      if (url.origin !== window.location.origin) return;
      if (url.pathname === window.location.pathname && url.search === window.location.search) return;
      if (!window.confirm(message)) {
        e.preventDefault();
        e.stopImmediatePropagation();
        e.stopPropagation();
      } else {
        dirtyRef.current = false;
      }
    };
    window.addEventListener("beforeunload", onBeforeUnload);
    document.addEventListener("click", onClick, true);
    return () => {
      window.removeEventListener("beforeunload", onBeforeUnload);
      document.removeEventListener("click", onClick, true);
    };
  }, [message]);

  useEffect(() => {
    const top = () => (window.history.state ?? {}) as GuardState;
    if (dirty && !top().__testlabGuard) {
      // Copying Next's own state keeps its router treating the entry as the same page.
      window.history.pushState({ ...top(), __testlabGuard: true }, "", window.location.href);
    } else if (!dirty && top().__testlabGuard) {
      ignoreNextPop.current = true;
      window.history.back();
    }
  }, [dirty]);

  useEffect(() => {
    const onPop = () => {
      if (ignoreNextPop.current) {
        ignoreNextPop.current = false;
        return;
      }
      const state = (window.history.state ?? {}) as GuardState;
      if (state.__testlabGuard || !dirtyRef.current) return;
      if (window.confirm(message)) {
        dirtyRef.current = false;
        window.history.back();
      } else {
        window.history.pushState({ ...state, __testlabGuard: true }, "", window.location.href);
      }
    };
    window.addEventListener("popstate", onPop);
    return () => window.removeEventListener("popstate", onPop);
  }, [message]);
}
