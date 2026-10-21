import { Menu } from "@base-ui/react/menu";
import { MoreVertical } from "lucide-react";

const ICON = 20;

export interface RowAction {
  key: string;
  label: string;
  onSelect: () => void;
  disabled?: boolean;
  /** Destructive: takes the row out of the heat. */
  danger?: boolean;
}

export function RowActionsMenu({
  displayName,
  actions,
}: {
  displayName: string;
  actions: readonly RowAction[];
}) {
  if (actions.length === 0) return null;
  // Not modal: a row menu must not lock the deck's scroll or shift its layout —
  // an outside tap simply closes it.
  return (
    <Menu.Root modal={false}>
      <Menu.Trigger className="row-menu-trigger" aria-label={`Actions for ${displayName}`}>
        <MoreVertical size={ICON} aria-hidden="true" />
      </Menu.Trigger>
      <Menu.Portal>
        <Menu.Positioner className="row-menu-positioner" sideOffset={4} align="end">
          <Menu.Popup className="row-menu">
            {actions.map((action) => (
              <Menu.Item
                key={action.key}
                className={`row-menu-item${action.danger ? " danger" : ""}`}
                disabled={action.disabled ?? false}
                onClick={action.onSelect}
              >
                {action.label}
              </Menu.Item>
            ))}
          </Menu.Popup>
        </Menu.Positioner>
      </Menu.Portal>
    </Menu.Root>
  );
}
