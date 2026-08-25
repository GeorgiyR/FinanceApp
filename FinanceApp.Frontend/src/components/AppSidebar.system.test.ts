import { describe, expect, it, vi } from 'vitest';
import {
  SYSTEM_PARENT_KEY,
  SYSTEM_PROCESS_JOURNAL_KEY,
  buildSidebarMenuItems,
  computeSidebarOpenKeys,
  isSystemSelectedKey,
} from './AppSidebar';

describe('AppSidebar system menu', () => {
  it('adds Система -> Журнал процессов leaf with /system/processes route', () => {
    const onNavigate = vi.fn();
    const items = buildSidebarMenuItems({ portfolios: [], marketIndices: [], onNavigate });

    const system = items?.find((item) => item?.key === SYSTEM_PARENT_KEY);
    expect(system).toBeTruthy();

    const leaf = (system as { children?: Array<{ key?: string; onClick?: () => void }> }).children?.find(
      (child) => child.key === SYSTEM_PROCESS_JOURNAL_KEY,
    );
    expect(leaf).toBeTruthy();

    leaf?.onClick?.();
    expect(onNavigate).toHaveBeenCalledWith('/system/processes');
  });

  it('opens system section for selected journal route', () => {
    const keys = computeSidebarOpenKeys({
      portfoliosOpen: false,
      stocksOpen: false,
      stocksDirectoriesOpen: false,
      marketIndicesOpen: false,
      selectedKeys: [SYSTEM_PROCESS_JOURNAL_KEY],
    });

    expect(keys).toContain(SYSTEM_PARENT_KEY);
    expect(isSystemSelectedKey(SYSTEM_PROCESS_JOURNAL_KEY)).toBe(true);
  });
});
