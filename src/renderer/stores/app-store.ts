import { create } from 'zustand';

interface AppState {
  visible: boolean;
  focused: boolean;
  setVisible: (v: boolean) => void;
  setFocused: (f: boolean) => void;
}

export const useAppStore = create<AppState>((set) => ({
  visible: true,
  focused: true,
  setVisible: (v) => set({ visible: v }),
  setFocused: (f) => set({ focused: f }),
}));
