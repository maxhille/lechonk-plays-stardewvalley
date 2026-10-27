{
  description = "Lechonk plays Stardew Valley - Mistral Large 4 harness + SMAPI mod";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" ];
      forSystems = f: nixpkgs.lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});
    in
    {
      # Phase 2: the Go harness (cmd/harness/). Module root is the repo root
      # (go.mod); the single main package lives at cmd/harness/. Built via the
      #   devshell:  go build -o ./lechonk ./cmd/harness
      #   (a flake package is added once a stable build recipe is settled).

      devShells = forSystems (pkgs: {
        default = pkgs.mkShell {
          packages = with pkgs; [
            go           # harness
            dotnet-sdk   # SMAPI mod: dotnet build, ModBuildConfig auto-deploys to Mods/
            curl         # poke the mod's HTTP API by hand
            jq
          ];
        };
      });

      # Runtime requirement, deliberately not a flake input:
      # Stardew Valley via Steam + SMAPI installed in the game folder.
      # Launch goes through Steam's environment (steam-run), not through nix;
      # the harness reads the launch command from SV_SMAPI_LAUNCH_CMD.
    };
}
