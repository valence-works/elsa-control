import { useEffect, useRef, useState } from "react";
import { ArrowUpRight, Search, X } from "lucide-react";
import { Link, useNavigate } from "react-router-dom";
import { consoleNavigation } from "@/app/consoleNavigation";

export function QuickNavigate({ open, onOpen, onClose }: { open: boolean; onOpen: () => void; onClose: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const input = useRef<HTMLInputElement>(null);
  const [query, setQuery] = useState("");
  const navigate = useNavigate();
  useEffect(() => {
    const handleKey = (event: KeyboardEvent) => {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "k") {
        event.preventDefault();
        if (open) onClose(); else onOpen();
      }
    };
    window.addEventListener("keydown", handleKey);
    return () => window.removeEventListener("keydown", handleKey);
  }, [open, onOpen, onClose]);
  useEffect(() => {
    if (open && !dialog.current?.open) { setQuery(""); dialog.current?.showModal(); input.current?.focus(); }
    if (!open && dialog.current?.open) dialog.current.close();
  }, [open]);
  const destinations = [{ to: "/admin/engines/connect", label: "Connect engine" }, ...consoleNavigation.flatMap(section => section.items)]
    .filter(item => item.label.toLowerCase().includes(query.toLowerCase()));
  return <dialog ref={dialog} className="console-command-dialog" aria-label="Go to a page" onCancel={onClose} onClose={onClose}>
    <div className="console-command-input"><Search aria-hidden size={18} /><input ref={input} value={query} onChange={event => setQuery(event.target.value)} onKeyDown={event => {
      if (event.key === "Enter" && destinations[0]) { event.preventDefault(); navigate(destinations[0].to); onClose(); }
      if (event.key === "ArrowDown") { event.preventDefault(); dialog.current?.querySelector<HTMLAnchorElement>("nav a")?.focus(); }
    }} placeholder="Where do you want to go?" aria-label="Find a page" /><button aria-label="Close search" className="console-icon-button" onClick={onClose}><X aria-hidden size={16} /></button></div>
    <nav aria-label="Search results" className="console-command-results">
      {destinations.map(item => <Link key={item.to} to={item.to} onClick={onClose}>{item.label}<ArrowUpRight aria-hidden size={14} /></Link>)}
      {!destinations.length && <p className="p-5 text-sm text-muted-foreground" role="status">No matching pages</p>}
    </nav>
  </dialog>;
}
