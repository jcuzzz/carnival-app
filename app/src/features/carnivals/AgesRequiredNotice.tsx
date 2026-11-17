import { href } from "../../router";

/** No saved ages and none from the school, so no event can be added yet. */
export function AgesRequiredNotice({ canSetAges }: { canSetAges: boolean }) {
  return (
    <div className="alert notice" role="status">
      <p className="flush-top">
        This carnival has no ages yet, so no event can be added. A carnival’s ages start as the
        school’s.
      </p>
      {canSetAges ? (
        <div className="button-row">
          <a className="button primary-link" href={href("/school/settings")}>
            Set the school's ages in School settings
          </a>
        </div>
      ) : (
        <p>A system administrator sets the school's ages in School settings.</p>
      )}
    </div>
  );
}
