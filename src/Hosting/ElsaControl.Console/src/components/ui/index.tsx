import { forwardRef } from "react";
import type { ButtonHTMLAttributes, InputHTMLAttributes, KeyboardEvent, ReactNode, SelectHTMLAttributes } from "react";
import { cn } from "@/lib/utils";

type EmptyStateTone = "neutral" | "warning" | "destructive";

export function buttonClassName(variant: "primary" | "secondary" = "primary", className?: string) {
  return cn(
    "inline-flex min-h-10 items-center justify-center gap-3 whitespace-nowrap rounded-ui border px-3.5 py-2 text-xs font-medium transition-colors duration-150 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-text disabled:pointer-events-none disabled:cursor-not-allowed disabled:opacity-60 aria-disabled:pointer-events-none aria-disabled:cursor-not-allowed aria-disabled:opacity-60",
    variant === "primary"
      ? "border-primary bg-primary text-primary-foreground hover:border-primary/90 hover:bg-primary/90"
      : "border-border bg-surface text-foreground hover:border-primary/45 hover:bg-muted",
    className
  );
}

export const Button = forwardRef<HTMLButtonElement, ButtonHTMLAttributes<HTMLButtonElement>>(function Button({ className, ...props }, ref) {
  return <button ref={ref} className={buttonClassName("primary", className)} {...props} />;
});

export const SecondaryButton = forwardRef<HTMLButtonElement, ButtonHTMLAttributes<HTMLButtonElement>>(function SecondaryButton({ className, ...props }, ref) {
  return <button ref={ref} className={buttonClassName("secondary", className)} {...props} />;
});

type InputProps = InputHTMLAttributes<HTMLInputElement> & {
  acceptPlaceholderOnTab?: boolean;
};

export function Input({ className, acceptPlaceholderOnTab = false, onKeyDown, type, ...props }: InputProps) {
  const handleKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    onKeyDown?.(event);
    if (event.defaultPrevented) return;
    if (!acceptPlaceholderOnTab) return;
    if (event.key !== "Tab" || event.shiftKey || event.altKey || event.ctrlKey || event.metaKey) return;
    if (type === "password") return;

    const placeholder = event.currentTarget.placeholder?.trim();
    if (!placeholder || event.currentTarget.value.trim().length > 0) return;

    event.preventDefault();
    const setNativeValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")?.set;
    setNativeValue?.call(event.currentTarget, placeholder);
    event.currentTarget.dispatchEvent(new Event("input", { bubbles: true }));
  };

  return (
    <input
      type={type}
      className={cn(
        "h-10 w-full rounded-ui border border-border bg-background px-3 text-sm text-foreground transition-[background-color,border-color,box-shadow] duration-150 placeholder:text-muted-foreground hover:border-primary/45 focus:border-primary-text focus:outline focus:outline-2 focus:outline-offset-2 focus:outline-primary-text disabled:cursor-not-allowed disabled:border-border disabled:bg-muted/45 disabled:text-muted-foreground disabled:opacity-80 read-only:bg-muted/25 aria-[invalid=true]:border-destructive aria-[invalid=true]:focus:border-destructive aria-[invalid=true]:focus:ring-destructive/20",
        className
      )}
      onKeyDown={handleKeyDown}
      {...props}
    />
  );
}

export function Select({ className, ...props }: SelectHTMLAttributes<HTMLSelectElement>) {
  return (
    <select
      className={cn(
        "h-10 rounded-ui border border-border bg-background px-3 text-sm text-foreground transition-[background-color,border-color,box-shadow] duration-150 hover:border-primary/45 focus:border-primary-text focus:outline focus:outline-2 focus:outline-offset-2 focus:outline-primary-text disabled:cursor-not-allowed disabled:border-border disabled:bg-muted/45 disabled:text-muted-foreground disabled:opacity-80 aria-[invalid=true]:border-destructive aria-[invalid=true]:focus:border-destructive aria-[invalid=true]:focus:ring-destructive/20",
        className
      )}
      {...props}
    />
  );
}

export function Badge({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <span className={cn("inline-flex min-h-6 items-center rounded-md border border-border bg-muted/35 px-2 py-0.5 text-xs font-medium leading-5", className)}>
      {children}
    </span>
  );
}

export function EmptyState({
  title,
  description,
  action,
  icon,
  className,
  role = "status",
  tone = "neutral"
}: {
  title: string;
  description: string;
  action?: ReactNode;
  icon?: ReactNode;
  className?: string;
  role?: "status" | "alert";
  tone?: EmptyStateTone;
}) {
  return (
    <div
      className={cn(
        "rounded-ui border border-border/80 bg-surface/80 px-6 py-9 text-center",
        className
      )}
      role={role}
      aria-live={role === "status" ? "polite" : undefined}
    >
      {icon ? (
        <div
          className={cn(
            "mx-auto mb-3 grid h-10 w-10 place-items-center rounded-full border",
            tone === "warning" && "border-warning/25 bg-warning/10 text-warning",
            tone === "destructive" && "border-destructive/25 bg-destructive/10 text-destructive",
            tone === "neutral" && "border-primary/20 bg-primary/10 text-primary"
          )}
        >
          {icon}
        </div>
      ) : null}
      <h2 className="font-display text-base font-semibold tracking-tight">{title}</h2>
      <p className="mx-auto mt-2 max-w-lg text-sm leading-6 text-muted-foreground">{description}</p>
      {action ? <div className="mt-5 flex justify-center">{action}</div> : null}
    </div>
  );
}

export function Table({ children }: { children: ReactNode }) {
  return (
    <div
      className={cn(
        "overflow-x-auto rounded-ui border border-border bg-surface",
        "[&_table]:min-w-full [&_table]:text-sm",
        "[&_thead]:border-b [&_thead]:border-border [&_thead]:bg-muted/35",
        "[&_th]:px-3 [&_th]:py-2.5 [&_th]:text-left [&_th]:font-mono [&_th]:text-[10px] [&_th]:font-semibold [&_th]:uppercase [&_th]:tracking-[0.08em] [&_th]:text-muted-foreground",
        "[&_tbody_tr]:border-b [&_tbody_tr]:border-border/70 [&_tbody_tr]:transition-colors [&_tbody_tr:hover]:bg-muted/30 [&_tbody_tr:last-child]:border-b-0",
        "[&_td]:align-middle [&_td]:px-3 [&_td]:py-3"
      )}
    >
      {children}
    </div>
  );
}

export function DialogPanel({ children }: { children: ReactNode }) {
  return <div className="rounded-ui border border-border bg-surface p-4 shadow-lg shadow-black/5">{children}</div>;
}

export function Tabs({ children }: { children: ReactNode }) {
  return <div className="flex gap-1 overflow-x-auto border-b border-border">{children}</div>;
}
