import type { MouseEvent } from "react";

export function permissionLinkProps(enabled: boolean) {
  return {
    "aria-disabled": !enabled,
    tabIndex: enabled ? undefined : -1,
    onClick: (event: MouseEvent<HTMLAnchorElement>) => {
      if (!enabled) event.preventDefault();
    }
  };
}
