import { useEffect, useState } from 'react';
import { Navigate, Outlet } from 'react-router-dom';
import { useAuthStore } from '../stores/auth-store';
import LoadingSpinner from './ui/LoadingSpinner';

export default function ProtectedRoute() {
  const { isAuthenticated, loadSavedSession } = useAuthStore();
  const [checking, setChecking] = useState(!isAuthenticated);

  useEffect(() => {
    if (!isAuthenticated) {
      loadSavedSession().then((restored) => {
        if (!restored) {
          // No session or restore failed — dismiss splash so login page shows
          window.dispatchEvent(new Event('nocturne:ready'));
        }
        setChecking(false);
      });
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
