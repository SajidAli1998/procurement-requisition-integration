import { createContext } from "react";

export const PinContext = createContext<(id: string) => void>(() => {});
