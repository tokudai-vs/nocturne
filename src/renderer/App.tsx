import { useEffect } from 'react';
import { createHashRouter, RouterProvider } from 'react-router-dom';
import ProtectedRoute from './components/ProtectedRoute';
import AppShell from './components/layout/AppShell';
import SplashScreen from './components/SplashScreen';
import MpvErrorModal from './components/ui/MpvErrorModal';
import PlaybackTransition from './components/PlaybackTransition';
import LoginPage from './pages/LoginPage';
import HomePage from './pages/HomePage';
import LibraryPage from './pages/LibraryPage';
import DetailPage from './pages/DetailPage';
import SearchPage from './pages/SearchPage';
import { useLibraryStore } from './stores/library-store';

const router = createHashRouter([
  {
    path: '/login',
    element: <LoginPage />,
  },
  {
    element: <ProtectedRoute />,
    children: [
      {
        element: <AppShell />,
        children: [
          { path: '/', element: <HomePage /> },
          { path: '/library/:id', element: <LibraryPage /> },
          { path: '/detail/:id', element: <DetailPage /> },
          { path: '/search', element: <SearchPage /> },
        ],
      },
    ],
  },
]);

function PlaybackExitListener() {
  useEffect(() => {
    const unsub = window.api.player.onExited(() => {
      const { fetchResume, fetchNextUp } = useLibraryStore.getState();
      fetchResume();
      fetchNextUp();
    });
    return unsub;
  }, []);
  return null;
}

export default function App() {
  return (
    <>
      <SplashScreen />
      <PlaybackExitListener />
      <PlaybackTransition />
      <MpvErrorModal />
      <RouterProvider router={router} />
    </>
  );
}
