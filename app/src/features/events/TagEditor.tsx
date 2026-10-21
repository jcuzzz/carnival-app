/** The faceted-tag editor shared by the event editor and the add-an-event screen. */

import { useState } from "react";
import { facetLabel, parseTag } from "./eventLabels";

export function TagEditor({
  tags,
  onChange,
  writable,
}: {
  tags: readonly string[];
  onChange: (tags: string[]) => void;
  writable: boolean;
}) {
  const [newTag, setNewTag] = useState("");

  function add(): void {
    const tag = newTag.trim();
    if (tag !== "" && !tags.includes(tag)) onChange([...tags, tag]);
    setNewTag("");
  }

  return (
    <div className="form-divider">
      <p className="row-sub">
        Tags classify the event for the list filters — “facet:value”, e.g. distance:50m.
      </p>
      <div className="button-row">
        {tags.length === 0 && <span className="row-sub">No tags.</span>}
        {tags.map((tag) => (
          <span key={tag} className="badge">
            {tagLabel(tag)}
            {writable && (
              <button type="button" onClick={() => onChange(tags.filter((t) => t !== tag))}>
                Remove {tagLabel(tag)}
              </button>
            )}
          </span>
        ))}
      </div>
      <div className="button-row">
        <label className="field wide flush-bottom">
          <span>Add a tag</span>
          <input
            value={newTag}
            placeholder="distance:50m"
            onChange={(e) => setNewTag(e.target.value)}
            onKeyDown={(e) => {
              // This input lives inside the screens' <form>: without this, Enter
              // implicitly submits and the typed tag is saved with nothing in it.
              if (e.key !== "Enter") return;
              e.preventDefault();
              add();
            }}
            disabled={!writable}
          />
        </label>
        <button type="button" disabled={!writable || newTag.trim() === ""} onClick={add}>
          Add tag
        </button>
      </div>
    </div>
  );
}

function tagLabel(tag: string): string {
  const { facet, value } = parseTag(tag);
  return `${facetLabel(facet)}: ${value}`;
}
