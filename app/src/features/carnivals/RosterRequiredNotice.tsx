import { ROSTER_REQUIRED_TEXT } from "../../api/errorText";
import { href } from "../../router";

/** ADR-0067 §5: no saved ages and no roster, so the student import comes first. */
export function RosterRequiredNotice({
  carnivalId,
  canImport,
}: {
  carnivalId: string;
  /** SystemAdministrator: imports are theirs alone, so only they get the link. */
  canImport: boolean;
}) {
  return (
    <div className="alert notice" role="status">
      <p className="flush-top">{ROSTER_REQUIRED_TEXT}</p>
      {canImport ? (
        <div className="button-row">
          <a className="button primary-link" href={href(`/import?carnival=${carnivalId}`)}>
            Import the students
          </a>
        </div>
      ) : (
        <p>A system administrator imports it.</p>
      )}
    </div>
  );
}
