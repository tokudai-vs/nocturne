import { useCallback } from 'react';
import type { BaseItemDto, ItemsResult } from '../api/types';

export function useEmby() {
  const getItem = useCallback(async (itemId: string): Promise<BaseItemDto | null> => {
    const res = await window.api.library.getItem(itemId);
    return res.success ? (res.data as BaseItemDto) : null;
  }, []);

  const getLatest = useCallback(
    async (parentId: string, limit?: number): Promise<BaseItemDto[]> => {
      const res = await window.api.library.getLatest(parentId, limit);
      return res.success ? (res.data as BaseItemDto[]) : [];
    },
    [],
  );

  const getSimilar = useCallback(async (itemId: string): Promise<BaseItemDto[]> => {
    const res = await window.api.library.getSimilar(itemId);
    return res.success ? (res.data as ItemsResult).Items : [];
  }, []);

  const getSeasons = useCallback(async (seriesId: string): Promise<BaseItemDto[]> => {
    const res = await window.api.library.getSeasons(seriesId);
    return res.success ? (res.data as ItemsResult).Items : [];
  }, []);

  const getEpisodes = useCallback(
    async (seriesId: string, seasonId: string): Promise<BaseItemDto[]> => {
      const res = await window.api.library.getEpisodes(seriesId, seasonId);
      return res.success ? (res.data as ItemsResult).Items : [];
    },
    [],
  );

  const search = useCallback(async (term: string): Promise<BaseItemDto[]> => {
    const res = await window.api.search.query(term);
    return res.success ? (res.data as ItemsResult).Items : [];
  }, []);

  const getStreamUrl = useCallback(
    async (itemId: string, mediaSourceId: string): Promise<string | null> => {
      const res = await window.api.media.getStreamUrl(itemId, mediaSourceId);
      return res.success ? (res.data as string) : null;
    },
    [],
  );

  const getImageUrl = useCallback(
    async (
      itemId: string,
      imageType: string,
      params?: Record<string, unknown>,
    ): Promise<string | null> => {
      const res = await window.api.image.getUrl(itemId, imageType, params);
      return res.success ? (res.data as string) : null;
    },
    [],
  );

  const markPlayed = useCallback(async (itemId: string) => {
    await window.api.user.markPlayed(itemId);
  }, []);

  const markUnplayed = useCallback(async (itemId: string) => {
    await window.api.user.markUnplayed(itemId);
  }, []);

  const toggleFavorite = useCallback(async (itemId: string, current: boolean) => {
    await window.api.user.updateFavorite(itemId, !current);
  }, []);

  return {
    getItem,
    getLatest,
    getSimilar,
    getSeasons,
    getEpisodes,
    search,
    getStreamUrl,
    getImageUrl,
    markPlayed,
    markUnplayed,
    toggleFavorite,
  };
}
