import { create } from 'zustand';
import type { BaseItemDto } from '../api/types';

interface ContextMenuState {
  visible: boolean;
  x: number;
  y: number;
  item: BaseItemDto | null;
  open: (x: number, y: number, item: BaseItemDto) => void;
  close: () => void;
}

export const useContextMenuStore = create<ContextMenuState>((set) => ({
  visible: false,
  x: 0,
  y: 0,
  item: null,
  open: (x, y, item) => set({ visible: true, x, y, item }),
  close: () => set({ visible: false, item: null }),
}));
