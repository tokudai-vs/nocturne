import { useEffect, useState } from 'react';
import { Navigate, Outlet } from 'react-router-dom';
import { useAuthStore } from '../stores/auth-store';
import { useToastStore } from '../stores/toast-store';
import LoadingSpinner from './ui/LoadingSpinner';

export default function ProtectedRoute() {
  const { isAuthenticated, loadSavedSession } = useAuthStore();
  const addToast = useToastStore((s) => s.addToast);
  const [checking, setChecking] = useState(!isAuthenticated);

  useEffect(() => {
    if (!isAuthenticated) {
      loadSavedSession().then((restored) => {
        if (!restored) {
          // No session or restore failed — dismiss splash so login page shows
          addToast('Session expired. Please sign in again.', 'error');
          window.dispatchEvent(new Event('nocturne:ready'));
        } else {
          // Auth restored — kick off background sync
          window.api.sync.autoStart();
        }
        setChecking(false);
      });
    } else {
      // Already authenticated (e.g. after login) — kick off sync
      window.api.sync.autoStart();
    }
  }, []);

  if (checking) {
    return (
      <div style={{ height: '100vh', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
        <LoadingSpinner size={48} />
      </div>
    );
  }

  if (!isAuthenticated) {
    return <Navigate to="/login" replace />;
  }

  return <Outlet />;
}
