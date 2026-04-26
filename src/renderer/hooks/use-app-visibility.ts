import { useAppStore } from '../stores/app-store';

export function useAppVisibility() {
  const visible = useAppStore((s) => s.visible);
  const focused = useAppStore((s) => s.focused);
  return { visible, focused, active: visible && focused };
}
