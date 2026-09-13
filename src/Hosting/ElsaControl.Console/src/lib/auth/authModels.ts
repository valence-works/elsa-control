export type CustomerAuthSession = {
  loginEnabled: boolean;
  authenticated: boolean;
  displayName: string | null;
  email: string | null;
  loginPath: string;
  logoutPath: string;
  isAdmin?: boolean;
};
