import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ConsoleLogsPage } from "@/features/console/ConsoleLogsPage";

const mocks = vi.hoisted(() => {
  const hubConnection = {
    state: "Connected",
    on: vi.fn(),
    onreconnecting: vi.fn(),
    onreconnected: vi.fn(),
    onclose: vi.fn(),
    start: vi.fn(async () => undefined),
    stop: vi.fn(async () => undefined),
    invoke: vi.fn(async () => undefined)
  };

  return {
    hubConnection,
    getRecentConsoleLogs: vi.fn(),
    listConsoleLogSources: vi.fn()
  };
});

vi.mock("@microsoft/signalr", () => ({
  HubConnectionState: { Connected: "Connected" },
  LogLevel: { Warning: 2 },
  HubConnectionBuilder: vi.fn(function () { return {
    withUrl: vi.fn().mockReturnThis(),
    withAutomaticReconnect: vi.fn().mockReturnThis(),
    configureLogging: vi.fn().mockReturnThis(),
    build: vi.fn(() => mocks.hubConnection)
  }; })
}));

vi.mock("@/features/console/consoleLogApi", () => ({
  consoleLogsHubPath: "/api/admin/console-logs/hub",
  getRecentConsoleLogs: mocks.getRecentConsoleLogs,
  listConsoleLogSources: mocks.listConsoleLogSources
}));

describe("ConsoleLogsPage", () => {
  afterEach(() => {
    mocks.getRecentConsoleLogs.mockReset();
    mocks.listConsoleLogSources.mockReset();
    vi.clearAllMocks();
  });

  it("renders recent stdout and stderr lines", async () => {
    renderConsoleLogsPage();

    expect(await screen.findByRole("heading", { name: "Console" })).toBeInTheDocument();
    expect(await screen.findByText("stdout line")).toBeInTheDocument();
    expect(screen.getByText("stderr line")).toBeInTheDocument();
    expect(screen.getByText("backend console")).toBeInTheDocument();
    await waitFor(() => expect(mocks.hubConnection.invoke).toHaveBeenCalledWith("SubscribeAsync", expect.objectContaining({ limit: 150 })));
  });

  it("updates the backend stream filter", async () => {
    renderConsoleLogsPage();

    await screen.findByText("stdout line");
    await userEvent.selectOptions(screen.getByRole("combobox", { name: "Stream" }), "stderr");

    await waitFor(() => {
      expect(mocks.getRecentConsoleLogs).toHaveBeenCalledWith(expect.objectContaining({ stream: 1 }));
      expect(mocks.hubConnection.invoke).toHaveBeenCalledWith("UpdateFilterAsync", expect.objectContaining({ stream: 1 }));
    });
  }, 10_000);
});

function renderConsoleLogsPage() {
  mocks.getRecentConsoleLogs.mockResolvedValue({
    items: [
      lineFixture("line-1", 0, "stdout line"),
      lineFixture("line-2", 1, "stderr line")
    ],
    sources: [{ id: "elsa-control-api", displayName: "Elsa Control API", health: "Connected" }]
  });
  mocks.listConsoleLogSources.mockResolvedValue([{ id: "elsa-control-api", displayName: "Elsa Control API", health: "Connected" }]);
  render(<ConsoleLogsPage />);
}

function lineFixture(id: string, stream: 0 | 1, text: string) {
  return {
    id,
    timestamp: "2026-06-07T12:00:00Z",
    receivedAt: "2026-06-07T12:00:00Z",
    sequence: stream + 1,
    stream,
    text,
    source: { id: "elsa-control-api", displayName: "Elsa Control API", health: "Connected" },
    truncated: false
  };
}
