import { useCallback } from 'react';
import { usePlayerStore } from '../stores/player-store';
import type { BaseItemDto, MediaSource } from '../api/types';

export function usePlay() {
  const { setCurrentItem, setPlaying, setError } = usePlayerStore();

  const play = useCallback(
    async (item: BaseItemDto, mediaSource?: MediaSource, serverId?: string) => {
      let sourceId = mediaSource?.Id;

      if (!sourceId) {
        const result = await window.api.media.getPlaybackInfo(item.Id, serverId);
        if (!result.success || !result.data?.MediaSources?.length) {
          setError('No media source available');
          return;
        }
        const src =
          result.data.MediaSources.find((s) => s.SupportsDirectPlay) ||
          result.data.MediaSources.find((s) => s.SupportsDirectStream) ||
          result.data.MediaSources[0];
        sourceId = src.Id;
      }

      setCurrentItem(item);
      setPlaying(true);

      const itemName =
        item.Type === 'Episode' && item.SeriesName
          ? `${item.SeriesName} - S${String(item.ParentIndexNumber ?? 0).padStart(2, '0')}E${String(item.IndexNumber ?? 0).padStart(2, '0')} - ${item.Name}`
          : item.Name;

      const result = await window.api.player.play({
        itemId: item.Id,
        mediaSourceId: sourceId,
        startPositionTicks: item.UserData?.PlaybackPositionTicks || 0,
        itemName,
        serverId,
      });

      if (!result.success) {
        setPlaying(false);
        setError(result.error ?? 'Playback failed');
      }
    },
    [setCurrentItem, setPlaying, setError],
  );

  return { play };
}
