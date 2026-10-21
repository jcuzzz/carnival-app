import { Component, type ErrorInfo, type ReactNode } from "react";

interface ErrorBoundaryProps {
  children: ReactNode;
}

interface ErrorBoundaryState {
  failed: boolean;
}

// The fallback links by literal hash href, not router.href(): the router may be what threw.
export default class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { failed: false };

  static getDerivedStateFromError(): ErrorBoundaryState {
    return { failed: true };
  }

  // Back, a nav tap or a typed address all change the hash; without this the
  // fallback outlives the screen that threw and only a reload leaves it.
  private readonly recover = (): void => {
    if (this.state.failed) this.setState({ failed: false });
  };

  componentDidMount(): void {
    window.addEventListener("hashchange", this.recover);
  }

  componentWillUnmount(): void {
    window.removeEventListener("hashchange", this.recover);
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error("Unhandled render error", error, info.componentStack);
  }

  render(): ReactNode {
    if (!this.state.failed) return this.props.children;
    return (
      <main className="screen" role="alert">
        <h1>Sorry — something went wrong</h1>
        <p>
          This screen stopped working. Nothing you entered has been lost: results and entries
          you saved are queued on this device and will sync once you carry on.
        </p>
        <p>
          <button type="button" className="primary" onClick={() => location.reload()}>
            Reload
          </button>
        </p>
        <p>
          <a href="#/outbox">Check your queued work</a>
        </p>
      </main>
    );
  }
}
