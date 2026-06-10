import { create } from 'zustand';

interface UiState {
  sidebarOpen: boolean;
  toggleSidebar: () => void;
  closeSidebar: () => void;
  /** Pages that want a solid (non-transparent) TopBar set this to true */
  topBarSolid: boolean;
  setTopBarSolid: (solid: boolean) => void;
  /** Splash screen visible */
  splashVisible: boolean;
  dismissSplash: () => void;
  /**
   * Cinema mode hides the AppShell's TopBar (and zeroes content padding) so
   * a player can take the full viewport. Set by the Watch Party host page
   * when state transitions to LIVE; cleared when leaving LIVE / unmounting.
   */
  cinemaMode: boolean;
  setCinemaMode: (on: boolean) => void;
}

export const useUiStore = create<UiState>((set) => ({
  sidebarOpen: false,
  toggleSidebar: () => set((s) => ({ sidebarOpen: !s.sidebarOpen })),
  closeSidebar: () => set({ sidebarOpen: false }),
  topBarSolid: false,
  setTopBarSolid: (solid) => set({ topBarSolid: solid }),
  splashVisible: true,
  dismissSplash: () => set({ splashVisible: false }),
  cinemaMode: false,
  setCinemaMode: (on) => set({ cinemaMode: on }),
}));
