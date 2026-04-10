import { useState, useEffect, useRef, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { Menu, X, Search, Film, Tv, PlayCircle } from 'lucide-react';
import { useAuthStore } from '../../stores/auth-store';
import { useUiStore } from '../../stores/ui-store';
import { buildImageUrl } from '../../utils/image-url';
import type { BaseItemDto, ItemsResult } from '../../api/types';
import UserMenu from './UserMenu';
import styles from './TopBar.module.css';

const TYPE_ICONS: Record<string, React.ReactNode> = {
  Movie: <Film size={14} />,
  Series: <Tv size={14} />,
  Episode: <PlayCircle size={14} />,
};

export default function TopBar() {
  const navigate = useNavigate();
  const { user } = useAuthStore();
  const { sidebarOpen, toggleSidebar, topBarSolid } = useUiStore();
  const [userMenuOpen, setUserMenuOpen] = useState(false);
  const [scrolled, setScrolled] = useState(false);

  // Search state
  const [searchTerm, setSearchTerm] = useState('');
  const [searchResults, setSearchResults] = useState<BaseItemDto[]>([]);
  const [showDropdown, setShowDropdown] = useState(false);
  const searchRef = useRef<HTMLInputElement>(null);
  const debounceRef = useRef<ReturnType<typeof setTimeout>>(undefined);

  // Scroll listener on content area
  useEffect(() => {
    const content = document.querySelector('.content-scroll');
    if (!content) return;
    const handler = () => setScrolled(content.scrollTop > 100);
    content.addEventListener('scroll', handler, { passive: true });
    return () => content.removeEventListener('scroll', handler);
  }, []);

  // Keyboard shortcuts
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.ctrlKey && e.key === 'b') { e.preventDefault(); toggleSidebar(); }
      if (e.ctrlKey && e.key === 'k') { e.preventDefault(); searchRef.current?.focus(); }
      if (e.altKey && e.key === 'ArrowLeft') { e.preventDefault(); navigate(-1); }
      if (e.altKey && e.key === 'ArrowRight') { e.preventDefault(); navigate(1); }
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [toggleSidebar, navigate]);

  // Search debounce
  const onSearchChange = useCallback((val: string) => {
    setSearchTerm(val);
    clearTimeout(debounceRef.current);
    if (!val.trim()) { setSearchResults([]); setShowDropdown(false); return; }
    debounceRef.current = setTimeout(async () => {
      const res = await window.api.search.query(val.trim());
      if (res.success) {
        setSearchResults((res.data as ItemsResult).Items.slice(0, 5));
        setShowDropdown(true);
      }
    }, 300);
  }, []);

  const handleSearchSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!searchTerm.trim()) return;
    setShowDropdown(false);
    searchRef.current?.blur();
    navigate(`/search?q=${encodeURIComponent(searchTerm.trim())}`);
  };

  const showSolid = topBarSolid || scrolled;
  const hideSearch = !topBarSolid && scrolled;

  const avatarUrl = user?.PrimaryImageTag
    ? buildImageUrl(user.Id, 'Primary', { maxWidth: 56, tag: user.PrimaryImageTag })
    : '';

  return (
    <header className={`${styles.bar} ${showSolid ? styles.barSolid : ''}`}>
      <div className={styles.dragRegion} />

      {/* Hamburger / X */}
      <button className={styles.iconBtn} onClick={toggleSidebar} title="Menu (Ctrl+B)">
        {sidebarOpen ? <X size={20} /> : <Menu size={20} />}
      </button>

      {/* Search */}
      <form className={`${styles.searchWrap} ${hideSearch ? styles.searchHidden : ''}`} onSubmit={handleSearchSubmit}>
        <span className={styles.searchIcon}><Search size={16} /></span>
        <input
          ref={searchRef}
          type="text"
          className={styles.searchInput}
          placeholder="Search movies, shows, episodes..."
          value={searchTerm}
          onChange={(e) => onSearchChange(e.target.value)}
          onFocus={() => searchResults.length > 0 && setShowDropdown(true)}
          onBlur={() => setTimeout(() => setShowDropdown(false), 200)}
        />
        {showDropdown && searchResults.length > 0 && (
          <div className={styles.dropdown}>
            {searchResults.map((item) => (
              <button
                key={item.Id}
                className={styles.dropdownItem}
                onMouseDown={() => { setShowDropdown(false); navigate(`/detail/${item.Id}`); }}
              >
                <div className={styles.dropdownThumb}>
                  {item.ImageTags?.['Primary'] ? (
                    <img src={buildImageUrl(item.Id, 'Primary', { maxWidth: 60, tag: item.ImageTags['Primary'] })} alt="" className={styles.dropdownImg} />
                  ) : (
                    <span>{item.Name.charAt(0)}</span>
                  )}
                </div>
                <div className={styles.dropdownInfo}>
                  <div className={styles.dropdownName}>{item.Name}</div>
                  <div className={styles.dropdownType}>
                    {TYPE_ICONS[item.Type] ?? null}
                    {item.Type}{item.ProductionYear ? ` \u00b7 ${item.ProductionYear}` : ''}
                  </div>
                </div>
              </button>
            ))}
          </div>
        )}
      </form>

      {/* Right: user + close */}
      <div className={styles.rightGroup}>
        <div className={styles.userWrap}>
          <button className={styles.avatarBtn} onClick={() => setUserMenuOpen(!userMenuOpen)}>
            {avatarUrl ? (
              <img src={avatarUrl} alt="" className={styles.avatarImg} />
            ) : (
              <span className={styles.avatarInit}>{user?.Name?.charAt(0) ?? '?'}</span>
            )}
          </button>
          {userMenuOpen && <UserMenu onClose={() => setUserMenuOpen(false)} />}
        </div>

        <button className={`${styles.winBtn} ${styles.closeBtn}`} onClick={() => window.api.window.close()} title="Close">
          <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.5"><line x1="3" y1="3" x2="11" y2="11"/><line x1="11" y1="3" x2="3" y2="11"/></svg>
        </button>
      </div>
    </header>
  );
}
