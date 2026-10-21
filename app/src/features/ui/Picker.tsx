import { Menu } from "@base-ui/react/menu";
import { Check, ChevronDown, Plus } from "lucide-react";

// The axis name is visually hidden on the trigger: "Boys" alone is not an accessible name.

const ICON = 20;

export interface PickerItem {
  key: string;
  label: string;
  /** Where the tap goes. Ignored on the current item — it is already here. */
  href?: string;
  current?: boolean;
  /** Only the division may claim to be the page; a heat is "true". */
  ariaCurrent?: "page" | "true";
  count?: string;
  complete?: boolean;
  badge?: string;
  /** Makes this an ACTION item rather than a link: "New heat" and nothing else yet. */
  onSelect?: () => void;
  disabled?: boolean;
  /** Said inside the item, so a dead action's reason is reachable on a tablet. */
  disabledReason?: string;
}

export function Picker({
  axisLabel,
  value,
  valueDetail,
  items,
  className,
}: {
  /** Omitted where the value says the axis itself — "Heat Heat 2" stutters. */
  axisLabel?: string;
  value: string;
  valueDetail?: string;
  items: readonly PickerItem[];
  className?: string;
}) {
  // Not modal: a header picker must not lock the deck's scroll or shift its
  // layout — an outside tap simply closes it.
  return (
    <Menu.Root modal={false}>
      <Menu.Trigger
        type="button"
        className={className ? `picker-trigger ${className}` : "picker-trigger"}
      >
        {/* The space is a SIBLING of the span: an accessible name concatenates
            each element's trimmed text, so one inside it would be lost. */}
        {axisLabel !== undefined && (
          <>
            <span className="visually-hidden">{axisLabel}</span>{" "}
          </>
        )}
        {value}
        {valueDetail !== undefined && (
          <>
            {" "}
            <span className="chip-count mono">{valueDetail}</span>
          </>
        )}
        <ChevronDown size={ICON} aria-hidden="true" />
      </Menu.Trigger>
      <Menu.Portal>
        <Menu.Positioner className="picker-positioner" sideOffset={4} align="start">
          <Menu.Popup className="picker-menu">
            {items.map((item) => (
              <PickerRow key={item.key} item={item} />
            ))}
          </Menu.Popup>
        </Menu.Positioner>
      </Menu.Portal>
    </Menu.Root>
  );
}

/** Visible here: with no border or chevron, a bare "16" between two pickers reads as stray text. */
export function PickerValue({ axisLabel, value }: { axisLabel: string; value: string }) {
  return (
    <span className="picker-trigger static">
      <span className="picker-axis">{axisLabel}</span> {value}
    </span>
  );
}

function PickerRow({ item }: { item: PickerItem }) {
  if (item.onSelect)
    return (
      <Menu.Item className="picker-item" disabled={item.disabled ?? false} onClick={item.onSelect}>
        <Plus size={ICON} aria-hidden="true" />
        {item.label}
        {item.disabledReason !== undefined && (
          <>
            {" "}
            <span className="picker-item-reason">{item.disabledReason}</span>
          </>
        )}
      </Menu.Item>
    );

  const body = (
    <>
      {item.label}
      {item.count !== undefined && (
        <>
          {" "}
          <span className="chip-count mono">{item.count}</span>
        </>
      )}
      {item.complete && <Check size={ICON} className="chip-tick" aria-hidden="true" />}
      {item.badge !== undefined && (
        <>
          {" "}
          <span className="badge ok">{item.badge}</span>
        </>
      )}
    </>
  );

  // No href either: an <a> with nothing to go to is a dead control, so the
  // item stays a plain menu item — the current item's own treatment.
  if (item.current || item.href === undefined)
    return (
      <Menu.Item
        className={item.current ? "picker-item current" : "picker-item"}
        closeOnClick
        {...(item.current && item.ariaCurrent ? { "aria-current": item.ariaCurrent } : {})}
      >
        {body}
      </Menu.Item>
    );
  return (
    // A hash href re-renders the deck under the popup rather than navigating
    // away from it, so the menu has to be told to close itself.
    <Menu.LinkItem className="picker-item" closeOnClick href={item.href}>
      {body}
    </Menu.LinkItem>
  );
}
